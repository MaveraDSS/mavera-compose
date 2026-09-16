using System.Text.Json;
using System.Text.Json.Nodes;

namespace Devenv;

/// <summary>One key of secrets.example.json: its 1Password reference (if any) and what it is for.</summary>
public sealed record SecretEntry(string Key, string? Reference, string Description);

/// <summary>A 1Password secret reference, op://vault/item/field (field may be section/field).</summary>
public sealed record OpReference(string Vault, string Item, string Field)
{
    public const string Scheme = "op://";

    public static bool Is(string? value) => value is not null && value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static OpReference? TryParse(string? value)
    {
        if (!Is(value)) return null;
        var parts = value![Scheme.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 3 ? null : new OpReference(parts[0], parts[1], string.Join('/', parts.Skip(2)));
    }

    public override string ToString() => $"{Scheme}{Vault}/{Item}/{Field}";
}

public sealed record SecretsMerge(
    List<KeyValuePair<string, string>> Values,
    List<string> Filled,
    List<string> Kept,
    /// <summary>Keys that still have no value; they are written with their op:// reference (or blank) so the file says where to look.</summary>
    List<string> Blank);

/// <summary>`devenv setup`, secrets part: secrets.example.json says where every value lives in 1Password; this fills secrets.json from it.</summary>
public static class SecretsSetup
{
    public const string ExampleFileName = "secrets.example.json";

    /// <summary>A value that gives the renderers nothing: blank, or a reference that was never resolved.</summary>
    public static bool IsUnresolved(string? value) => string.IsNullOrWhiteSpace(value) || OpReference.Is(value);

    /// <summary>
    /// Reads secrets.example.json in file order. A real key's value is its op:// reference or blank. Keys starting
    /// with "_" are comments: "_KEY" describes that one key, any other "_Group" describes the keys that follow it.
    /// </summary>
    public static List<SecretEntry> ParseExample(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
                ?? throw new DevenvException($"{ExampleFileName} is empty");
        }
        catch (JsonException ex)
        {
            throw new DevenvException($"{ExampleFileName} is not valid JSON: {ex.Message}");
        }

        var entries = new List<SecretEntry>();
        var group = "";
        foreach (var (key, node) in root)
        {
            var value = AsString(node);
            if (key.StartsWith('_'))
            {
                // "_KEY" belongs to KEY alone; everything else opens a group.
                if (key != "_comment" && !root.ContainsKey(key[1..])) group = value;
                continue;
            }
            var own = AsString(root["_" + key]);
            entries.Add(new SecretEntry(key, OpReference.Is(value) ? value : null, own.Length > 0 ? own : group));
        }
        return entries;
    }

    private static string AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The reference to read for a key: a developer's own op:// value in secrets.json wins over the example's.</summary>
    public static string? ReferenceFor(SecretEntry entry, IReadOnlyDictionary<string, string> existing) =>
        existing.TryGetValue(entry.Key, out var v) && OpReference.Is(v) ? v : entry.Reference;

    /// <summary>
    /// New content of secrets.json: example order; a real value already there is kept, a resolved one fills a gap,
    /// anything still unresolved is written as its reference (or blank). Keys unknown to the example are kept at the end.
    /// </summary>
    public static SecretsMerge Merge(IReadOnlyList<SecretEntry> example, IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> resolved)
    {
        var values = new List<KeyValuePair<string, string>>();
        var filled = new List<string>();
        var kept = new List<string>();
        var blank = new List<string>();
        foreach (var e in example)
        {
            existing.TryGetValue(e.Key, out var current);
            if (!IsUnresolved(current))
            {
                values.Add(new(e.Key, current!));
                kept.Add(e.Key);
            }
            else if (resolved.TryGetValue(e.Key, out var value) && !IsUnresolved(value))
            {
                values.Add(new(e.Key, value));
                filled.Add(e.Key);
            }
            else
            {
                values.Add(new(e.Key, ReferenceFor(e, existing) ?? ""));
                blank.Add(e.Key);
            }
        }
        foreach (var (key, value) in existing)
        {
            if (!key.StartsWith('_') && example.All(e => e.Key != key))
            {
                values.Add(new(key, value));
                kept.Add(key);
            }
        }
        return new SecretsMerge(values, filled, kept, blank);
    }

    public static string ToJson(SecretsMerge merge, string environmentName)
    {
        var obj = new JsonObject
        {
            ["_comment"] = $"Written by `devenv setup` for {environmentName} from {ExampleFileName}. Gitignored. A value still reading op://... was not found in 1Password: fill it by hand, or fix the reference (in this file or the example) and run setup again.",
        };
        foreach (var (key, value) in merge.Values)
        {
            obj[key] = value;
        }
        return obj.ToJsonString(Json.Options);
    }

    /// <summary>Raw secrets.json (op:// values included, "_" keys dropped); empty when the file does not exist.</summary>
    public static Dictionary<string, string> ReadExisting(string secretsFile)
    {
        if (!File.Exists(secretsFile)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return Json.Load<Dictionary<string, string?>>(secretsFile)
            .Where(kv => !kv.Key.StartsWith('_'))
            .ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.Ordinal);
    }

    /// <summary>Values worth importing from another secrets.json: real values only, for keys the example knows.</summary>
    public static Dictionary<string, string> Importable(IReadOnlyDictionary<string, string> imported, IReadOnlyList<SecretEntry> example) =>
        imported.Where(kv => !IsUnresolved(kv.Value) && example.Any(e => e.Key == kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>Fills secrets.json; returns the required keys that are still blank afterwards.</summary>
    public static List<string> Run(Workspace ws, bool interactive, string? importFile = null)
    {
        var exampleFile = Path.Combine(ws.Root, ExampleFileName);
        if (!File.Exists(exampleFile)) throw new DevenvException($"{exampleFile} is missing");
        var example = ParseExample(File.ReadAllText(exampleFile));
        var existing = ReadExisting(ws.SecretsFile);
        var required = ws.RequiredSecretKeys;

        var todo = example.Where(e => !existing.TryGetValue(e.Key, out var v) || IsUnresolved(v)).ToList();
        if (todo.Count == 0)
        {
            Console.WriteLine($"  {Path.GetFileName(ws.SecretsFile)} complete ({example.Count} keys), nothing to do");
            return new List<string>();
        }
        Console.WriteLine($"  {todo.Count} of {example.Count} keys without a value{(File.Exists(ws.SecretsFile) ? "" : $" ({Path.GetFileName(ws.SecretsFile)} does not exist yet)")}");

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (importFile is not null)
        {
            // Sources in order: an imported file, then the keyboard. Existing values always win (Merge).
            var fullPath = Path.GetFullPath(importFile);
            if (!File.Exists(fullPath)) throw new DevenvException($"--secrets {importFile}: file not found");
            if (string.Equals(fullPath, Path.GetFullPath(ws.SecretsFile), StringComparison.OrdinalIgnoreCase))
            {
                throw new DevenvException($"--secrets {importFile} is this workspace's own secrets.json; point it at the file to import from");
            }
            var importable = Importable(ReadExisting(fullPath), example);
            foreach (var e in todo.Where(e => importable.ContainsKey(e.Key)))
            {
                resolved[e.Key] = importable[e.Key];
            }
            Console.WriteLine($"  {fullPath}: {resolved.Count} value(s) imported{(importable.Count > resolved.Count ? $", {importable.Count - resolved.Count} already set here and kept" : "")}");
        }
        // Prompt only for what `up` cannot do without; optional values stay as references until someone needs them.
        var stillMissing = todo.Where(e => !resolved.ContainsKey(e.Key) && required.Contains(e.Key)).ToList();
        if (stillMissing.Count > 0 && interactive)
        {
            Console.WriteLine($"  {stillMissing.Count} required value(s) to enter by hand (input is hidden; Enter skips):");
            foreach (var e in stillMissing)
            {
                var typed = Prompt(e, ReferenceFor(e, existing));
                if (typed is not null) resolved[e.Key] = typed;
            }
        }

        var merge = Merge(example, existing, resolved);
        File.WriteAllText(ws.SecretsFile, ToJson(merge, ws.Environment.Name));
        Console.WriteLine($"  wrote {ws.SecretsFile}: {merge.Filled.Count} filled, {merge.Kept.Count} kept, {merge.Blank.Count} still without a value");

        var blankRequired = merge.Blank.Where(required.Contains).ToList();
        var blankOptional = merge.Blank.Except(blankRequired).ToList();
        if (blankOptional.Count > 0)
        {
            Console.WriteLine($"  optional, only some --local services need them: {string.Join(", ", blankOptional)}");
        }
        if (blankRequired.Count > 0)
        {
            Console.WriteLine($"  REQUIRED and still blank: {string.Join(", ", blankRequired)}");
            Console.WriteLine($"  fill them in {ws.SecretsFile} (the example file says where they live) or fix the op:// reference and run setup again");
        }
        return blankRequired;
    }

    private static string? Prompt(SecretEntry entry, string? reference)
    {
        Console.WriteLine();
        Console.WriteLine($"  {entry.Key}");
        if (entry.Description.Length > 0) Console.WriteLine($"    {entry.Description}");
        if (reference is not null) Console.WriteLine($"    1Password: {reference}");
        Console.Write("    value: ");
        var value = ReadHidden();
        Console.WriteLine(value.Length == 0 ? "(skipped)" : "(set)");
        return value.Length == 0 ? null : value;
    }

    private static string ReadHidden()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray()).Trim();
    }
}

/// <summary>`devenv setup`, Claude Code part: a CLAUDE.md for the repos root, so a session opened there knows what the folder holds and which skills to use.</summary>
public static class ReposRootClaudeMd
{
    public const string FileName = "CLAUDE.md";

    public static string Render(Manifest manifest)
    {
        var repos = new List<string> { manifest.Frontend.Repo, manifest.Gateway.Repo, "mavera-compose" };
        repos.AddRange(manifest.Services.Where(s => s.Project is not null).Select(s => s.Repo));
        var lines = new List<string>
        {
            "# CLAUDE.md — repos root",
            "",
            "This folder holds the Mavera DSS repositories side by side; it is not a git repository itself. Written by",
            "`devenv setup` (mavera-compose/devenv); edit freely, it is yours.",
            "",
            "- `mavera-compose/devenv` runs the frontend, libertine and chosen services locally against dev02:",
            "  `dotnet run --project mavera-compose/devenv/src/Devenv -- <up|status|logs|down> --root mavera-compose/devenv`.",
            "- Claude Code plugin `dss` (from mavera-compose): `/dss:dev-env <ticket>` starts the right stack for a ticket,",
            "  `/dss:verify <ticket>` checks the running stack with evidence, `/dss:ticket <ticket>` works a ticket end to end.",
            "",
            "## Rules for working from here",
            "",
            "- Each repository has its own conventions. Before editing or committing in one, read its `CLAUDE.md` in full",
            "  (for the frontend also `apps/dss/CLAUDE.md`); those rules are not loaded automatically from this folder.",
            "- Use explicit paths: `git -C <repo> ...`, `dotnet test <repo>/...`, `pnpm -C <repo> ...`, `rg <pattern> <repo>/`.",
            "- Never commit on `develop`/`master`, never push or open a PR unless asked, no `Co-Authored-By` trailers.",
            "",
            "## Repositories devenv knows",
            "",
        };
        lines.AddRange(repos.Distinct().Select(r => $"- `{r}`"));
        lines.Add("");
        return string.Join('\n', lines);
    }

    /// <summary>Writes the file when the repos root has none; never overwrites a developer's own.</summary>
    public static bool WriteIfMissing(Manifest manifest, string reposRoot)
    {
        var path = Path.Combine(reposRoot, FileName);
        if (File.Exists(path)) return false;
        File.WriteAllText(path, Render(manifest));
        return true;
    }
}

/// <summary>`devenv setup`, frontend part: the pnpm workspace install, only when node_modules is missing or behind the lockfile.</summary>
public static class FrontendPackages
{
    public static (bool Needed, string Reason) NeedsInstall(string repoPath)
    {
        var nodeModules = Path.Combine(repoPath, "node_modules");
        if (!Directory.Exists(nodeModules)) return (true, "node_modules missing");
        var wanted = Path.Combine(repoPath, "pnpm-lock.yaml");
        if (!File.Exists(wanted)) return (false, "no pnpm-lock.yaml in the repo, nothing to compare against");
        // pnpm keeps the lockfile it installed from next to the packages; equal files mean nothing to do.
        var installed = Path.Combine(nodeModules, ".pnpm", "lock.yaml");
        if (!File.Exists(installed)) return (true, "node_modules present but without pnpm's install record (node_modules/.pnpm/lock.yaml)");
        return FilesEqual(wanted, installed)
            ? (false, "node_modules up to date with pnpm-lock.yaml")
            : (true, "pnpm-lock.yaml changed since the last install");
    }

    public static async Task InstallAsync(string repoPath)
    {
        Console.WriteLine("  corepack pnpm install --frozen-lockfile  (a few minutes the first time)");
        var (code, output) = await ProcessRunner.RunAsync("corepack", new[] { "pnpm", "install", "--frozen-lockfile" }, repoPath, TimeSpan.FromMinutes(20), echo: true);
        if (code != 0)
        {
            throw new DevenvException($"pnpm install failed (exit {code}): {output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}. Is Node 24 installed and this a new terminal? Zscaler connected (the registry may sit behind it)?");
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var ba = new byte[81920];
        var bb = new byte[81920];
        while (true)
        {
            var ra = sa.Read(ba, 0, ba.Length);
            var rb = sb.Read(bb, 0, bb.Length);
            if (ra != rb) return false;
            if (ra == 0) return true;
            if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
        }
    }
}
