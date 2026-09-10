namespace Devenv;

/// <summary>Everything loaded from disk: manifest, environment, local settings, secrets, and resolved paths.</summary>
public sealed class Workspace
{
    public string Root { get; }
    public string ReposRoot { get; }
    public Manifest Manifest { get; }
    public EnvironmentSpec Environment { get; }
    public LocalSettings Local { get; }
    public IReadOnlyDictionary<string, string> Secrets { get; }
    public bool HasSecretsFile { get; }
    public string DeveloperName { get; }
    public string StateDir => Path.Combine(Root, ".state");
    public string StateFile => Path.Combine(StateDir, "processes.json");
    public string LogDir => Path.Combine(StateDir, "logs");
    public string SecretsFile => Path.Combine(Root, "secrets.json");
    public string InfraComposeFile => Path.Combine(Root, "infra", "docker-compose.yml");
    public const string InfraProjectName = "mavera-devenv";

    private Workspace(string root, string reposRoot, Manifest manifest, EnvironmentSpec env, LocalSettings local,
        IReadOnlyDictionary<string, string> secrets, bool hasSecretsFile, string developerName)
    {
        Root = root;
        ReposRoot = reposRoot;
        Manifest = manifest;
        Environment = env;
        Local = local;
        Secrets = secrets;
        HasSecretsFile = hasSecretsFile;
        DeveloperName = developerName;
    }

    public static Workspace Load(CliOptions options)
    {
        var root = options.Root ?? System.Environment.GetEnvironmentVariable("DEVENV_ROOT") ?? FindRoot()
            ?? throw new DevenvException("cannot find manifest.json; run from inside the devenv folder or pass --root <path>");
        root = Path.GetFullPath(root);

        var manifest = Json.Load<Manifest>(Path.Combine(root, "manifest.json"));
        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new DevenvException("manifest.json is invalid:\n  - " + string.Join("\n  - ", errors));
        }

        var localFile = Path.Combine(root, "devenv.local.json");
        var local = File.Exists(localFile) ? Json.Load<LocalSettings>(localFile) : new LocalSettings();
        if (options.Environment is not null)
        {
            local.Environment = options.Environment;
        }
        if (options.ReposRoot is not null)
        {
            local.ReposRoot = options.ReposRoot;
        }
        if (options.NoInfra)
        {
            local.Infra = false;
        }
        if (options.Local.Count > 0)
        {
            local.LocalServices = options.Local.ToList();
        }

        var envFile = Path.Combine(root, "environments", local.Environment + ".json");
        if (!File.Exists(envFile))
        {
            var known = Directory.GetFiles(Path.Combine(root, "environments"), "*.json")
                .Select(Path.GetFileNameWithoutExtension);
            throw new DevenvException($"unknown environment '{local.Environment}'; known: {string.Join(", ", known)}");
        }
        var env = Json.Load<EnvironmentSpec>(envFile);

        var secretsFile = Path.Combine(root, "secrets.json");
        var hasSecrets = File.Exists(secretsFile);
        var secrets = hasSecrets
            ? Json.Load<Dictionary<string, string>>(secretsFile)
                .Where(kv => !kv.Key.StartsWith('_'))
                .ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var reposRoot = Path.GetFullPath(local.ReposRoot ?? Path.Combine(root, "..", ".."));
        var developer = local.DeveloperName ?? System.Environment.UserName;

        foreach (var name in local.LocalServices)
        {
            if (manifest.FindService(name) is null)
            {
                throw new DevenvException($"unknown local service '{name}'; known: {string.Join(", ", manifest.Services.Select(s => s.Name))}");
            }
        }

        return new Workspace(root, reposRoot, manifest, env, local, secrets, hasSecrets, developer);
    }

    /// <summary>Walks up from the current directory, then from the binary's directory, looking for manifest.json.</summary>
    public static string? FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "manifest.json")) && Directory.Exists(Path.Combine(dir.FullName, "environments")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    public string RepoPath(string repo) => Path.Combine(ReposRoot, repo);

    public IReadOnlyList<ServiceSpec> LocalServices =>
        Local.LocalServices.Select(n => Manifest.FindService(n)!).ToList();

    public string FrontendOrigin => $"http://localhost:{Manifest.Frontend.Port}";
    public string GatewayOrigin => $"http://localhost:{Manifest.Gateway.Port}";

    public string RequireSecret(string key)
    {
        if (!HasSecretsFile)
        {
            throw new DevenvException($"secrets.json not found at {SecretsFile}; copy secrets.example.json to secrets.json and fill it from 1Password (see README)");
        }
        if (!Secrets.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new DevenvException($"secret '{key}' is missing or blank in {SecretsFile}; see secrets.example.json for where it comes from");
        }
        return value;
    }
}
