using System.Text.RegularExpressions;

namespace Devenv;

public sealed record PlaceholderResolution(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> MissingRequiredSecrets,
    IReadOnlyList<string> Blank);

/// <summary>
/// Every service's committed appsettings.json is an envsubst template over one shared vocabulary of
/// $Placeholder tokens (163 of them, listed in the fleet compose file's x-placeholders block). This builds
/// the value table for "this service runs on my machine against a remote environment", in layers:
///   1. defaults from x-placeholders (compose ${VAR:-default} resolved to the default)
///   2. environments/&lt;env&gt;.json placeholders (hosts, database, Okta server)
///   3. manifest placeholders.local (local RabbitMQ, Redis, log level)
///   4. the service's own placeholders map in the manifest
///   5. computed per machine (telemetry endpoint, service environment name)
///   6. secrets.json through manifest placeholders.secrets
///   7. with --db local: manifest placeholders.localDatabase (server, user, password of the container)
/// </summary>
public static partial class PlaceholderTable
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::?([-?+])([^}]*))?\}")]
    private static partial Regex ComposeVariable();

    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    public static partial Regex Token();

    /// <summary>Parses the flat `x-placeholders:` map of a compose file. Only that block is read; the rest of the YAML is ignored.</summary>
    public static Dictionary<string, string> ParseComposeDefaults(string composeYaml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inBlock = false;
        foreach (var raw in composeYaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inBlock)
            {
                inBlock = line.StartsWith("x-placeholders:", StringComparison.Ordinal);
                continue;
            }
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                break; // next top-level key
            }
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var key = trimmed[..colon].Trim();
            if (key.StartsWith('&') || key.StartsWith("<<", StringComparison.Ordinal))
            {
                continue;
            }
            var value = StripComment(trimmed[(colon + 1)..].Trim());
            value = Unquote(value);
            value = ComposeVariable().Replace(value, m => m.Groups[2].Value is "-" or "+" ? m.Groups[3].Value : "");
            result[key] = value;
        }
        return result;
    }

    private static string StripComment(string value)
    {
        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            return end > 0 ? value[..(end + 1)] : value;
        }
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    /// <summary>Names of every placeholder a template references.</summary>
    public static HashSet<string> TokensIn(string template) =>
        Token().Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    public static PlaceholderResolution Resolve(
        IReadOnlyDictionary<string, string> composeDefaults,
        EnvironmentSpec environment,
        PlaceholderSpec spec,
        IReadOnlyDictionary<string, string> serviceOverrides,
        IReadOnlyDictionary<string, string> computed,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlySet<string> used,
        IReadOnlyDictionary<string, string>? finalOverrides = null)
    {
        var values = new Dictionary<string, string>(composeDefaults, StringComparer.Ordinal);
        foreach (var (k, v) in environment.Placeholders) values[k] = v;
        foreach (var (k, v) in spec.Local) values[k] = v;
        foreach (var (k, v) in serviceOverrides) values[k] = v;
        foreach (var (k, v) in computed) values[k] = v;

        var missing = new List<string>();
        foreach (var (placeholder, secret) in spec.Secrets)
        {
            if (secrets.TryGetValue(secret.Key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                values[placeholder] = v;
            }
            else if (secret.Required && used.Contains(placeholder))
            {
                missing.Add($"{secret.Key} (for ${placeholder})");
            }
            else if (used.Contains(placeholder) && !values.ContainsKey(placeholder))
            {
                values[placeholder] = "";
            }
        }

        if (finalOverrides is not null)
        {
            // --db local: the container's server, user and password beat even the secrets layer, and a
            // missing remote DB password is no longer a problem.
            foreach (var (k, v) in finalOverrides)
            {
                values[k] = v;
                missing.RemoveAll(m => m.EndsWith($"(for ${k})", StringComparison.Ordinal));
            }
        }

        var blank = used.Where(u => !values.TryGetValue(u, out var v) || string.IsNullOrEmpty(v)).OrderBy(x => x).ToList();
        return new PlaceholderResolution(values, missing, blank);
    }
}
