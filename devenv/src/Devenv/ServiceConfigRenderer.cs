using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Devenv;

public sealed record ServiceRenderInput(
    ServiceSpec Service,
    string TemplateJson,
    IReadOnlyDictionary<string, string> Placeholders,
    Manifest Manifest,
    EnvironmentSpec Environment,
    IReadOnlyList<ServiceSpec> LocalServices,
    string GeneratedNote,
    string? SqlHost = null);

public sealed record ServiceRenderResult(string Json, IReadOnlyList<string> Warnings);

/// <summary>
/// Renders a backend service's appsettings.Development.json from its committed template, the same way the
/// cluster does (envsubst), then re-points every in-cluster hostname: a service running on this machine
/// becomes http://localhost:&lt;port&gt;, the identity server becomes the environment's authority, everything
/// else the environment's internal ingress with the template's path kept. The message broker host must end
/// up on localhost; a rendered config that still points at a cluster broker is refused.
/// </summary>
public static partial class ServiceConfigRenderer
{
    [GeneratedRegex(@"^(https?)://([A-Za-z0-9.-]+?)\.svc\.cluster\.local(?::\d+)?(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClusterUrl();

    [GeneratedRegex(@"^(https?)://([A-Za-z0-9-]+)(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareHostUrl();

    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*$")]
    private static partial Regex BareHost();

    public static ServiceRenderResult Render(ServiceRenderInput input)
    {
        var warnings = new List<string>();
        var used = PlaceholderTable.TokensIn(input.TemplateJson);

        var substituted = PlaceholderTable.Token().Replace(input.TemplateJson, m =>
            input.Placeholders.TryGetValue(m.Groups[1].Value, out var v) ? EscapeForJsonString(v) : m.Value);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(substituted, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
                ?? throw new DevenvException($"{input.Service.Name}: template is not a JSON object");
        }
        catch (JsonException ex)
        {
            throw new DevenvException($"{input.Service.Name}: template is not valid JSON after substitution: {ex.Message}");
        }

        var ctx = new Context(input, warnings);
        RewriteStrings(root, ctx);

        var output = new JsonObject { ["_generated"] = input.GeneratedNote };
        foreach (var (k, v) in root.ToList())
        {
            root.Remove(k);
            output[k] = v;
        }

        var json = output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        foreach (var token in used)
        {
            if (json.Contains("$" + token, StringComparison.Ordinal))
            {
                throw new DevenvException($"{input.Service.Name}: placeholder ${token} has no value; add it to environments/{input.Environment.Name}.json, manifest placeholders.local, or secrets.json");
            }
        }
        if (json.Contains("svc.cluster.local", StringComparison.OrdinalIgnoreCase))
        {
            throw new DevenvException($"{input.Service.Name}: a cluster-internal hostname survived rendering; the template has a URL shape this renderer does not understand");
        }
        CheckBroker(output, input.Service.Name);
        CheckCache(output, input.Service.Name);
        CheckConnectionStrings(output, input, warnings);

        return new ServiceRenderResult(json, warnings);
    }

    private sealed record Context(ServiceRenderInput Input, List<string> Warnings)
    {
        public Dictionary<string, ServiceSpec> ServiceByHost { get; } =
            Input.Manifest.Services.ToDictionary(s => s.Repo, s => s, StringComparer.OrdinalIgnoreCase);
    }

    private static void RewriteStrings(JsonNode? node, Context ctx)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        var rewritten = RewriteValue(s, ctx);
                        if (!ReferenceEquals(rewritten, s) && rewritten != s)
                        {
                            obj[key] = rewritten;
                        }
                    }
                    else
                    {
                        RewriteStrings(child, ctx);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        var rewritten = RewriteValue(s, ctx);
                        if (rewritten != s) arr[i] = rewritten;
                    }
                    else
                    {
                        RewriteStrings(arr[i], ctx);
                    }
                }
                break;
        }
    }

    private static string RewriteValue(string s, Context ctx)
    {
        var cluster = ClusterUrl().Match(s);
        if (cluster.Success)
        {
            return Target(cluster.Groups[2].Value, cluster.Groups[3].Value, bare: false, ctx);
        }
        var hosts = ctx.Input.Manifest.Placeholders.Hosts;
        if (BareHost().IsMatch(s) && (hosts.ContainsKey(s) || ctx.ServiceByHost.ContainsKey(s)))
        {
            return Target(s, "", bare: true, ctx);
        }
        var bareUrl = BareHostUrl().Match(s);
        if (bareUrl.Success && (hosts.ContainsKey(bareUrl.Groups[2].Value) || ctx.ServiceByHost.ContainsKey(bareUrl.Groups[2].Value)))
        {
            return Target(bareUrl.Groups[2].Value, bareUrl.Groups[3].Value, bare: false, ctx);
        }
        return s;
    }

    /// <param name="bare">The template value was a hostname without scheme; a literal mapping is returned verbatim and a service target gets its remotePath.</param>
    private static string Target(string host, string path, bool bare, Context ctx)
    {
        var env = ctx.Input.Environment;
        if (ctx.Input.Manifest.Placeholders.Hosts.TryGetValue(host, out var mapped))
        {
            var baseUrl = mapped switch
            {
                "@identity" => env.IdentityAuthority,
                "@internal" => env.InternalBaseUrl,
                "@public" => env.PublicBaseUrl,
                _ => mapped,
            };
            if (bare && !mapped.StartsWith('@') && !mapped.Contains("://", StringComparison.Ordinal))
            {
                return mapped; // e.g. the broker host: "localhost"
            }
            return Join(baseUrl, path);
        }

        if (ctx.ServiceByHost.TryGetValue(host, out var service))
        {
            var local = ctx.Input.LocalServices.FirstOrDefault(l => l.Name == service.Name);
            var baseUrl = local is not null ? $"http://localhost:{local.Port}" : env.InternalBaseUrl;
            if (bare)
            {
                return Join(baseUrl, service.RemotePath ?? "").TrimEnd('/');
            }
            return Join(baseUrl, path);
        }

        ctx.Warnings.Add($"unknown cluster host '{host}' sent to the internal ingress; add it to manifest placeholders.hosts if that is wrong");
        return Join(env.InternalBaseUrl, path);
    }

    private static string Join(string baseUrl, string path)
    {
        if (path.Length == 0) return baseUrl.TrimEnd('/');
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private static void CheckBroker(JsonObject root, string service)
    {
        foreach (var section in new[] { "Messaging", "MessageBroker" })
        {
            var host = root[section]?["Host"]?.GetValue<string>();
            if (host is null) continue;
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1")
            {
                throw new DevenvException($"{service}: {section}:Host would be '{host}'. A local service must only ever use the local RabbitMQ (it registers consumers and would steal the environment's messages). Fix manifest placeholders.hosts.");
            }
        }
    }

    /// <summary>Same rule as the broker: a local service may only use the local Redis, never the cluster's.</summary>
    private static void CheckCache(JsonObject root, string service)
    {
        var host = root["DistributedCacheProjectSettings"]?["CacheService"]?.GetValue<string>();
        if (host is null || host.Length == 0) return;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1")
        {
            throw new DevenvException($"{service}: DistributedCacheProjectSettings:CacheService would be '{host}'. A local service must only use the local Redis. Fix manifest placeholders.local (RedisInstance).");
        }
    }

    private static void CheckConnectionStrings(JsonObject root, ServiceRenderInput input, List<string> warnings)
    {
        if (root["ConnectionStrings"] is not JsonObject cs) return;
        var expectedHost = input.SqlHost ?? input.Environment.Sql.Host;
        foreach (var (name, value) in cs)
        {
            var text = value?.GetValue<string>() ?? "";
            var server = Regex.Match(text, @"(?:Server|Data Source)=(?:tcp:)?([^,;]+)", RegexOptions.IgnoreCase);
            if (server.Success && !server.Groups[1].Value.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"ConnectionStrings:{name} points at '{server.Groups[1].Value}', not the expected database host {expectedHost}");
            }
        }
    }

    private static string EscapeForJsonString(string value)
    {
        var quoted = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return quoted[1..^1];
    }
}
