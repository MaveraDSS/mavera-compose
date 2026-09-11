using Devenv;
using Xunit;

namespace Devenv.Tests;

public class ManifestValidatorTests
{
    [Fact]
    public void CommittedManifestIsValid()
    {
        var errors = ManifestValidator.Validate(Fixture.Manifest());
        Assert.Empty(errors);
    }

    [Fact]
    public void DuplicatePortIsRejected()
    {
        var m = Fixture.Manifest();
        m.Services[1].Port = m.Services[0].Port;
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("port") && e.Contains("already used"));
    }

    [Fact]
    public void PortCollidingWithGatewayIsRejected()
    {
        var m = Fixture.Manifest();
        m.Services[0].Port = m.Gateway.Port;
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("already used by gateway"));
    }

    [Fact]
    public void DuplicateClusterClaimIsRejected()
    {
        var m = Fixture.Manifest();
        m.Services[1].ClusterIds.Add(m.Services[0].ClusterIds[0]);
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("cluster") && e.Contains("already claimed"));
    }

    [Fact]
    public void DuplicatePublicPrefixIsRejected()
    {
        var m = Fixture.Manifest();
        m.Services[1].PublicPrefix = m.Services[0].PublicPrefix;
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("publicPrefix") && e.Contains("already claimed"));
    }

    [Theory]
    [InlineData("Evaluation Service")]
    [InlineData("evaluation_service")]
    [InlineData("-evaluation")]
    [InlineData("")]
    public void ServiceNameMustBeKebabCase(string name)
    {
        var m = Fixture.Manifest();
        m.Services[0].Name = name;
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("kebab-case"));
    }

    [Fact]
    public void ProjectMustStayInsideRepo()
    {
        var m = Fixture.Manifest();
        m.Services[0].Project = "../somewhere/else";
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("relative path inside the repo"));
    }

    [Fact]
    public void UnknownIngressIsRejected()
    {
        var m = Fixture.Manifest();
        m.Gateway.ServiceSettings["Monolith"].Ingress = "somewhere";
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("ingress must be"));
    }

    [Theory]
    [InlineData("liquibase")]
    [InlineData("DBUP")]
    public void UnknownMigrationKindIsRejected(string kind)
    {
        var m = Fixture.Manifest();
        m.Services[0].Migrations = kind;
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("migrations must be"));
    }

    [Fact]
    public void JournalMustBeAPlainTableName()
    {
        var m = Fixture.Manifest();
        m.Services[0].MigrationsJournal = "dbo.SchemaVersions; DROP TABLE x";
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("migrationsJournal"));
    }

    [Fact]
    public void EverySecretPlaceholderNamesAKey()
    {
        var m = Fixture.Manifest();
        m.Placeholders.Secrets["X"] = new SecretPlaceholder { Key = "" };
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("placeholders.secrets.X"));
    }

    [Fact]
    public void EveryClusterInTheLibertineTemplateIsEitherClaimedOrKnownExternal()
    {
        // Documents which template clusters have no repo behind them; if libertine gains a cluster,
        // this test fails until the manifest (or this list) is updated.
        var external = new[] { "mavera-pdf-search", "mavera-idp2-inference" };
        var template = Fixture.Parse(Fixture.LibertineTemplate());
        var claimed = Fixture.Manifest().Services.SelectMany(s => s.ClusterIds).ToHashSet();
        var unclaimed = template.RootElement.GetProperty("ReverseProxy").GetProperty("Clusters").EnumerateObject()
            .Select(p => p.Name)
            .Where(id => !claimed.Contains(id) && !external.Contains(id))
            .ToList();
        Assert.Empty(unclaimed);
    }
}
