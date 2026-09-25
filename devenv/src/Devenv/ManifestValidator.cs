using System.Text.RegularExpressions;

namespace Devenv;

public static partial class ManifestValidator
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex Identifier();

    public static List<string> Validate(Manifest m)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(m.Frontend.Repo)) errors.Add("frontend.repo is required");
        if (m.Frontend.Command.Count == 0) errors.Add("frontend.command is required");
        if (!ValidPort(m.Frontend.Port)) errors.Add("frontend.port must be between 1024 and 65535");

        if (string.IsNullOrWhiteSpace(m.Gateway.Repo)) errors.Add("gateway.repo is required");
        if (string.IsNullOrWhiteSpace(m.Gateway.Project)) errors.Add("gateway.project is required");
        if (string.IsNullOrWhiteSpace(m.Gateway.ConfigTemplate)) errors.Add("gateway.configTemplate is required");
        if (string.IsNullOrWhiteSpace(m.Gateway.ConfigOutput)) errors.Add("gateway.configOutput is required");
        if (!ValidPort(m.Gateway.Port)) errors.Add("gateway.port must be between 1024 and 65535");
        if (m.Gateway.EdgeRoutes.Count == 0) errors.Add("gateway.edgeRoutes must list at least one prefix");
        if (m.Gateway.MaxRequestBodySize <= 0) errors.Add("gateway.maxRequestBodySize must be positive");
        foreach (var (name, o) in m.Gateway.ServiceSettings)
        {
            if (o.Ingress is not null and not ("public" or "internal"))
            {
                errors.Add($"gateway.serviceSettings.{name}.ingress must be 'public' or 'internal'");
            }
        }

        var ports = new Dictionary<int, string> { [m.Frontend.Port] = "frontend", [m.Gateway.Port] = "gateway" };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in m.Services)
        {
            var label = string.IsNullOrWhiteSpace(s.Name) ? "<unnamed service>" : s.Name;
            if (string.IsNullOrWhiteSpace(s.Name) || !KebabCase().IsMatch(s.Name))
            {
                errors.Add($"service '{label}': name must be kebab-case (letters, digits, dashes)");
            }
            if (!names.Add(s.Name)) errors.Add($"service '{label}': duplicate name");
            if (string.IsNullOrWhiteSpace(s.Repo)) errors.Add($"service '{label}': repo is required");
            if (!ValidPort(s.Port)) errors.Add($"service '{label}': port must be between 1024 and 65535");
            else if (ports.TryGetValue(s.Port, out var other)) errors.Add($"service '{label}': port {s.Port} already used by {other}");
            else ports[s.Port] = s.Name;

            foreach (var c in s.ClusterIds)
            {
                if (clusters.TryGetValue(c, out var owner)) errors.Add($"service '{label}': cluster '{c}' already claimed by {owner}");
                else clusters[c] = s.Name;
            }
            if (s.PublicPrefix is not null)
            {
                var p = s.PublicPrefix.Trim('/');
                if (p.Length == 0) errors.Add($"service '{label}': publicPrefix must not be empty");
                else if (prefixes.TryGetValue(p, out var owner)) errors.Add($"service '{label}': publicPrefix '{p}' already claimed by {owner}");
                else prefixes[p] = s.Name;
            }
            if (s.Project is not null && (Path.IsPathRooted(s.Project) || s.Project.Contains("..")))
            {
                errors.Add($"service '{label}': project must be a relative path inside the repo");
            }
            if (s.Migrations is not null and not ("dbup" or "efcore" or "fluentmigrator"))
            {
                errors.Add($"service '{label}': migrations must be 'dbup', 'efcore' or 'fluentmigrator'");
            }
            if (s.MigrationsJournal is not null && !Identifier().IsMatch(s.MigrationsJournal))
            {
                errors.Add($"service '{label}': migrationsJournal must be a plain table name");
            }
        }

        foreach (var (placeholder, secret) in m.Placeholders.Secrets)
        {
            if (string.IsNullOrWhiteSpace(secret.Key))
            {
                errors.Add($"placeholders.secrets.{placeholder}: key is required");
            }
        }
        foreach (var (host, target) in m.Placeholders.Hosts)
        {
            if (target.StartsWith('@') && target is not ("@identity" or "@internal" or "@public"))
            {
                errors.Add($"placeholders.hosts.{host}: unknown target {target}");
            }
        }

        return errors;
    }

    private static bool ValidPort(int port) => port is >= 1024 and <= 65535;
}
