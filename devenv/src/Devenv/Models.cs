using System.Text.Json;
using System.Text.Json.Serialization;

namespace Devenv;

/// <summary>Thrown for user-facing errors; the CLI prints the message and exits with code 2.</summary>
public sealed class DevenvException : Exception
{
    public DevenvException(string message) : base(message) { }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new DevenvException($"missing file: {path}");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                ?? throw new DevenvException($"empty JSON document: {path}");
        }
        catch (JsonException ex)
        {
            throw new DevenvException($"invalid JSON in {path}: {ex.Message}");
        }
    }
}

// ---------------------------------------------------------------------------
// manifest.json — what exists (committed)
// ---------------------------------------------------------------------------

public sealed class Manifest
{
    public FrontendSpec Frontend { get; set; } = new();
    public GatewaySpec Gateway { get; set; } = new();
    public List<ServiceSpec> Services { get; set; } = new();
    public PlaceholderSpec Placeholders { get; set; } = new();
    /// <summary>Prefix for cloning missing repos: gitBase + repo + ".git".</summary>
    public string GitBase { get; set; } = "https://github.com/MaveraDSS/";

    public ServiceSpec? FindService(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class FrontendSpec
{
    public string Repo { get; set; } = "";
    /// <summary>Branch checked out when devenv clones the repo (an existing clone is never switched).</summary>
    public string Branch { get; set; } = "develop";
    /// <summary>Directory inside the repo the start command runs in.</summary>
    public string WorkingDir { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Start command; the CLI sets the environment, no shell script involved.</summary>
    public List<string> Command { get; set; } = new();
    public string EnvFile { get; set; } = ".env";
    public string HealthPath { get; set; } = "/";
    public FrontendSecrets Secrets { get; set; } = new();
}

public sealed class FrontendSecrets
{
    /// <summary>Keys copied from secrets.json into the .env; rendering fails when one is missing or blank.</summary>
    public List<string> Required { get; set; } = new();
    /// <summary>Keys copied when present; written blank otherwise.</summary>
    public List<string> Optional { get; set; } = new();
}

public sealed class GatewaySpec
{
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "develop";
    public string Project { get; set; } = "";
    public int Port { get; set; }
    /// <summary>The committed envsubst template; its cluster list is the source of truth.</summary>
    public string ConfigTemplate { get; set; } = "";
    /// <summary>Where the rendered Development config goes (gitignored in that repo).</summary>
    public string ConfigOutput { get; set; } = "";
    public string HealthPath { get; set; } = "";
    /// <summary>A path proxied to the remote environment; proves the edge works.</summary>
    public string EdgeHealthPath { get; set; } = "";
    /// <summary>Path prefixes the frontend calls outside /libertine; forwarded to the remote public host.</summary>
    public List<string> EdgeRoutes { get; set; } = new();
    /// <summary>Clusters that must target the public host rather than the internal ingress.</summary>
    public List<string> PublicClusters { get; set; } = new();
    /// <summary>Per ServiceSettings entry: ingress choice and/or path override.</summary>
    public Dictionary<string, ServiceSettingsOverride> ServiceSettings { get; set; } = new();
    public long MaxRequestBodySize { get; set; } = 524288000;
    public string OktaInternalSecretKey { get; set; } = "OKTA_INTERNAL_SECRET";
}

public sealed class ServiceSettingsOverride
{
    /// <summary>"public" or "internal" (default internal).</summary>
    public string? Ingress { get; set; }
    /// <summary>Path appended to the host, replacing whatever the template has.</summary>
    public string? Path { get; set; }
}

public sealed class ServiceSpec
{
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    /// <summary>Branch checked out when devenv clones the repo (an existing clone is never switched).</summary>
    public string Branch { get; set; } = "develop";
    /// <summary>Project to `dotnet run`; null until someone runs the service locally for the first time.</summary>
    public string? Project { get; set; }
    public int Port { get; set; }
    /// <summary>Path prefix the frontend calls directly (without /libertine), e.g. Vera/EvaluationService.</summary>
    public string? PublicPrefix { get; set; }
    /// <summary>YARP cluster ids in libertine's template that point at this service.</summary>
    public List<string> ClusterIds { get; set; } = new();
    /// <summary>Entries in libertine's ServiceSettings.Services that point at this service.</summary>
    public List<string> ServiceSettingsNames { get; set; } = new();
    public string? HealthPath { get; set; }
    /// <summary>Path of this service on the remote internal ingress, used when another service's template names it by bare hostname.</summary>
    public string? RemotePath { get; set; }
    /// <summary>"dbup" or "efcore" when the service applies migrations at startup; null otherwise.</summary>
    public string? Migrations { get; set; }
    /// <summary>DbUp: folder with the .sql scripts, relative to the project directory (default _Migrations).</summary>
    public string? MigrationsFolder { get; set; }
    /// <summary>DbUp: journal table holding applied script names (default SchemaVersions).</summary>
    public string? MigrationsJournal { get; set; }
    /// <summary>Connection string name in the rendered config used for the migration preflight (default MaveraContext).</summary>
    public string? ConnectionStringName { get; set; }
    /// <summary>Config template relative to the project directory (default appsettings.json).</summary>
    public string ConfigTemplate { get; set; } = "appsettings.json";
    public string ConfigOutput { get; set; } = "appsettings.Development.json";
    public bool RunsMigrations => Migrations is not null;
    public bool UsesMessageBroker { get; set; }
    public bool SendsMail { get; set; }
    /// <summary>True when the service carries x64-only assemblies (Service Fabric remoting); on an arm64 Mac it runs under the x64 .NET host.</summary>
    public bool RequiresX64 { get; set; }
    /// <summary>Placeholder values that apply to this service only, on top of manifest placeholders.local.</summary>
    public Dictionary<string, string> Placeholders { get; set; } = new();
    public string? Notes { get; set; }

    public string ProjectDir => Project is null ? "" : (Path.GetDirectoryName(Project) ?? "");
}

/// <summary>How $Placeholder tokens in the services' committed appsettings.json templates get their values.</summary>
public sealed class PlaceholderSpec
{
    /// <summary>Compose file whose x-placeholders block supplies the defaults, relative to the devenv folder.</summary>
    public string Source { get; set; } = "";
    /// <summary>Values that make a service talk to the local infra instead of the cluster's.</summary>
    public Dictionary<string, string> Local { get; set; } = new();
    /// <summary>Placeholder → secrets.json key.</summary>
    public Dictionary<string, SecretPlaceholder> Secrets { get; set; } = new();
    /// <summary>Cluster hostname → target: "@identity", "@internal", "@public", or a literal such as "localhost".</summary>
    public Dictionary<string, string> Hosts { get; set; } = new();
}

public sealed class SecretPlaceholder
{
    public string Key { get; set; } = "";
    public bool Required { get; set; }
}

// ---------------------------------------------------------------------------
// environments/<name>.json — where the remote side is (committed, no secrets)
// ---------------------------------------------------------------------------

public sealed class EnvironmentSpec
{
    public string Name { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public string InternalBaseUrl { get; set; } = "";
    public string IdentityAuthority { get; set; } = "";
    public OktaSpec Okta { get; set; } = new();
    public SqlSpec Sql { get; set; } = new();
    /// <summary>Non-secret frontend variables that differ per environment.</summary>
    public Dictionary<string, string> Frontend { get; set; } = new();
    /// <summary>Placeholder values specific to this environment (hosts, database, Okta server).</summary>
    public Dictionary<string, string> Placeholders { get; set; } = new();
    /// <summary>A URL that only answers when the VPN/Zscaler is connected.</summary>
    public string ConnectivityCheckUrl { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class OktaSpec
{
    public string Authority { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Audiences { get; set; } = "";
    public string InternalClientId { get; set; } = "";
    public string FrontendClientId { get; set; } = "";
}

public sealed class SqlSpec
{
    public string Host { get; set; } = "";
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
}

// ---------------------------------------------------------------------------
// devenv.local.json — this machine (gitignored)
// ---------------------------------------------------------------------------

public sealed class LocalSettings
{
    /// <summary>Folder that holds the cloned repos side by side. Default: the parent of mavera-compose.</summary>
    public string? ReposRoot { get; set; }
    public string Environment { get; set; } = "dev02";
    /// <summary>Shows up as Local-DEV-&lt;name&gt; in telemetry. Default: the OS user name.</summary>
    public string? DeveloperName { get; set; }
    public List<string> LocalServices { get; set; } = new();
    public bool Infra { get; set; } = true;
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    /// <summary>Path of the x64 `dotnet` host used for services with requiresX64 on an arm64 machine. Default: the side-by-side install location.</summary>
    public string? DotnetX64 { get; set; }
}

// ---------------------------------------------------------------------------
// .state/processes.json — what `up` started (gitignored)
// ---------------------------------------------------------------------------

public sealed class RunState
{
    public int SupervisorPid { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string Environment { get; set; } = "";
    public bool Infra { get; set; }
    public List<RunningProcess> Processes { get; set; } = new();
}

public sealed class RunningProcess
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public int Port { get; set; }
    public string? HealthUrl { get; set; }
    public string LogFile { get; set; } = "";
}
