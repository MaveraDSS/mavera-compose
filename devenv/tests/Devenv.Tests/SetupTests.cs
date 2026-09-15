using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class SetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devenv-setup-tests-" + Guid.NewGuid().ToString("N"));

    public SetupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ------------------------------------------------------------ references

    [Theory]
    [InlineData("op://k8s-secrets-dev/Dev02/ConnectionStrings_DB_Pass", "k8s-secrets-dev", "Dev02", "ConnectionStrings_DB_Pass")]
    [InlineData("op://vault/item/section/field", "vault", "item", "section/field")]
    [InlineData("OP://Vault/Item With Spaces/Field", "Vault", "Item With Spaces", "Field")]
    public void ReferenceParsesVaultItemAndField(string text, string vault, string item, string field)
    {
        var r = OpReference.TryParse(text);
        Assert.NotNull(r);
        Assert.Equal((vault, item, field), (r!.Vault, r.Item, r.Field));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("op://vault/item")]
    [InlineData("https://vault/item/field")]
    public void ReferenceRejectsAnythingElse(string text)
    {
        Assert.Null(OpReference.TryParse(text));
    }

    [Fact]
    public void AReferenceOrABlankIsUnresolvedARealValueIsNot()
    {
        Assert.True(SecretsSetup.IsUnresolved(""));
        Assert.True(SecretsSetup.IsUnresolved("  "));
        Assert.True(SecretsSetup.IsUnresolved(null));
        Assert.True(SecretsSetup.IsUnresolved("op://v/i/f"));
        Assert.False(SecretsSetup.IsUnresolved("hunter2"));
    }

    // --------------------------------------------------------------- example

    [Fact]
    public void ExampleParsesInOrderWithReferencesAndDescriptions()
    {
        var entries = SecretsSetup.ParseExample("""
            {
              "_comment": "header",
              "_A": "about a",
              "A": "op://v/i/A",
              "_GROUP": "about the group",
              "B": "op://v/i/B",
              "C": "",
              "_D": "about d",
              "D": "not-a-reference"
            }
            """);
        Assert.Equal(new[] { "A", "B", "C", "D" }, entries.Select(e => e.Key));
        Assert.Equal("op://v/i/A", entries[0].Reference);
        Assert.Equal("about a", entries[0].Description);
        Assert.Equal("about the group", entries[1].Description);
        Assert.Null(entries[2].Reference);
        Assert.Equal("about the group", entries[2].Description);
        Assert.Null(entries[3].Reference); // a literal example value is not a reference
        Assert.Equal("about d", entries[3].Description);
    }

    [Fact]
    public void TheCommittedExampleCoversEveryKeyTheManifestNeeds()
    {
        var m = Fixture.Manifest();
        var entries = SecretsSetup.ParseExample(Fixture.Read("secrets.example.json"));
        var keys = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var needed = m.Frontend.Secrets.Required.Concat(m.Frontend.Secrets.Optional)
            .Concat(m.Placeholders.Secrets.Values.Select(s => s.Key))
            .Append(m.Gateway.OktaInternalSecretKey)
            .Distinct();
        Assert.True(needed.All(keys.Contains), $"missing from secrets.example.json: {string.Join(", ", needed.Where(k => !keys.Contains(k)))}");
        Assert.Equal(entries.Count, keys.Count); // no key twice
        // Every reference must be well formed; the one personal value (own e-mail address) is the only key without one.
        foreach (var e in entries.Where(e => e.Reference is not null))
        {
            Assert.NotNull(OpReference.TryParse(e.Reference));
        }
        Assert.Equal(new[] { "MAIL_TEST_ADDRESS" }, entries.Where(e => e.Reference is null).Select(e => e.Key));
        Assert.All(entries, e => Assert.NotEqual("", e.Description));
    }

    // ----------------------------------------------------------------- merge

    private static readonly List<SecretEntry> Example = new()
    {
        new("A", "op://v/i/A", "a"),
        new("B", "op://v/i/B", "b"),
        new("C", null, "c"),
    };

    [Fact]
    public void MergeKeepsExistingValuesFillsGapsAndWritesReferencesForTheRest()
    {
        var existing = new Dictionary<string, string> { ["A"] = "keep-me", ["B"] = "", ["EXTRA"] = "mine" };
        var resolved = new Dictionary<string, string> { ["A"] = "would-overwrite", ["B"] = "fresh" };
        var merge = SecretsSetup.Merge(Example, existing, resolved);
        Assert.Equal(new[] { "A", "B", "C", "EXTRA" }, merge.Values.Select(v => v.Key));
        Assert.Equal("keep-me", merge.Values[0].Value);
        Assert.Equal("fresh", merge.Values[1].Value);
        Assert.Equal("", merge.Values[2].Value);
        Assert.Equal("mine", merge.Values[3].Value);
        Assert.Equal(new[] { "B" }, merge.Filled);
        Assert.Equal(new[] { "A", "EXTRA" }, merge.Kept);
        Assert.Equal(new[] { "C" }, merge.Blank);
    }

    [Fact]
    public void MergeWritesTheReferenceWhenNothingResolvedSoTheFileSaysWhereToLook()
    {
        var merge = SecretsSetup.Merge(Example, new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.Equal("op://v/i/A", merge.Values[0].Value);
        Assert.Equal(new[] { "A", "B", "C" }, merge.Blank);
    }

    [Fact]
    public void ADevelopersOwnReferenceInSecretsJsonWinsOverTheExample()
    {
        var existing = new Dictionary<string, string> { ["A"] = "op://Private/mine/A" };
        Assert.Equal("op://Private/mine/A", SecretsSetup.ReferenceFor(Example[0], existing));
        Assert.Equal("op://v/i/B", SecretsSetup.ReferenceFor(Example[1], existing));
        var merge = SecretsSetup.Merge(Example, existing, new Dictionary<string, string>());
        Assert.Equal("op://Private/mine/A", merge.Values[0].Value);
    }

    [Fact]
    public void ImportTakesRealValuesForKnownKeysOnly()
    {
        var imported = new Dictionary<string, string> { ["A"] = "from-file", ["B"] = "op://v/i/B", ["C"] = "", ["UNKNOWN"] = "x" };
        var importable = SecretsSetup.Importable(imported, Example);
        Assert.Equal(new[] { "A" }, importable.Keys);
    }

    [Fact]
    public void SecretsOptionParses()
    {
        var o = CliOptions.Parse(new[] { "setup", "--secrets", "/tmp/from-1password/secrets.json", "--no-prompt" });
        Assert.Equal("setup", o.Command);
        Assert.Equal("/tmp/from-1password/secrets.json", o.SecretsImport);
        Assert.True(o.NoPrompt);
    }

    [Fact]
    public void WrittenJsonRoundTripsAndStartsWithTheComment()
    {
        var merge = SecretsSetup.Merge(Example, new Dictionary<string, string> { ["A"] = "x" }, new Dictionary<string, string>());
        var json = SecretsSetup.ToJson(merge, "dev02");
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "_comment", "A", "B", "C" }, props);
        Assert.Equal("x", doc.RootElement.GetProperty("A").GetString());

        var file = Path.Combine(_root, "secrets.json");
        File.WriteAllText(file, json);
        var back = SecretsSetup.ReadExisting(file);
        Assert.Equal(new[] { "A", "B", "C" }, back.Keys.OrderBy(k => k));
        Assert.Equal("x", back["A"]);
    }

    [Fact]
    public void ReadExistingIsEmptyWithoutAFile()
    {
        Assert.Empty(SecretsSetup.ReadExisting(Path.Combine(_root, "nope.json")));
    }

    // ------------------------------------------------------------- packages

    [Fact]
    public void PackagesAreInstalledWhenNodeModulesIsMissing()
    {
        var (needed, reason) = FrontendPackages.NeedsInstall(_root);
        Assert.True(needed);
        Assert.Contains("node_modules missing", reason);
    }

    [Fact]
    public void PackagesAreInstalledWhenTheLockfileChanged()
    {
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\nnew: true\n");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", ".pnpm"));
        File.WriteAllText(Path.Combine(_root, "node_modules", ".pnpm", "lock.yaml"), "lockfileVersion: '9.0'\nold: true\n");
        var (needed, reason) = FrontendPackages.NeedsInstall(_root);
        Assert.True(needed);
        Assert.Contains("changed", reason);
    }

    [Fact]
    public void PackagesAreLeftAloneWhenTheInstalledLockfileMatches()
    {
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\nsame: true\n");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", ".pnpm"));
        File.Copy(Path.Combine(_root, "pnpm-lock.yaml"), Path.Combine(_root, "node_modules", ".pnpm", "lock.yaml"));
        var (needed, _) = FrontendPackages.NeedsInstall(_root);
        Assert.False(needed);
    }

    [Fact]
    public void PackagesAreInstalledWhenNodeModulesHasNoInstallRecord()
    {
        File.WriteAllText(Path.Combine(_root, "pnpm-lock.yaml"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        var (needed, reason) = FrontendPackages.NeedsInstall(_root);
        Assert.True(needed);
        Assert.Contains("install record", reason);
    }
}
