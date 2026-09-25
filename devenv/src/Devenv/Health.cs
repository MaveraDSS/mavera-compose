using System.Net;
using System.Text.Json;

namespace Devenv;

/// <summary>
/// Ok: the process answers well enough to work with. Degraded: named health-check entries that failed but are
/// tolerated for this run (e.g. document-service's `s3` while the S3 secrets are blank); empty when nothing failed.
/// </summary>
public sealed record HealthResult(bool Ok, string Detail, IReadOnlyList<string>? Degraded = null)
{
    public bool IsDegraded => Degraded is { Count: > 0 };
}

public static class Health
{
    private static readonly IReadOnlyDictionary<string, string> NoTolerance = new Dictionary<string, string>();

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>One GET; 2xx/3xx is healthy. 4xx counts as "answering" for the frontend root (auth redirects) but not for health endpoints.</summary>
    public static async Task<HealthResult> ProbeAsync(string url, bool acceptClientErrors = false, CancellationToken ct = default, IReadOnlyDictionary<string, string>? tolerated = null)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            var ok = code < 400 || (acceptClientErrors && code < 500);
            if (ok || tolerated is not { Count: > 0 })
            {
                return new HealthResult(ok, $"HTTP {code}");
            }
            // A 503 from an ASP.NET health endpoint lists which checks failed; read it to see whether they are all tolerated ones.
            var body = await response.Content.ReadAsStringAsync(ct);
            return Classify(code, body, tolerated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult(false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new HealthResult(false, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Reads the detailed health JSON the services write (`{"status":..., "entries":{"s3":{"status":"Unhealthy",...}}}`).
    /// The result is Ok when every entry that is not Healthy is in <paramref name="tolerated"/> (name → note); the note
    /// goes into Detail so `up` and `status` say why it is fine. Anything else (no JSON, an untolerated failure) fails.
    /// </summary>
    public static HealthResult Classify(int code, string? body, IReadOnlyDictionary<string, string> tolerated)
    {
        var failing = FailingEntries(body);
        if (failing is null || failing.Count == 0)
        {
            return new HealthResult(false, $"HTTP {code}");
        }
        var untolerated = failing.Where(f => !tolerated.ContainsKey(f)).ToList();
        if (untolerated.Count > 0)
        {
            return new HealthResult(false, $"HTTP {code}, failing: {string.Join(", ", failing)}");
        }
        var notes = failing.Select(f => $"{f} ({tolerated[f]})");
        return new HealthResult(true, $"HTTP {code}, degraded: {string.Join("; ", notes)}", failing);
    }

    /// <summary>Names of the entries whose status is not Healthy; null when the body is not that JSON shape.</summary>
    public static List<string>? FailingEntries(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var failing = new List<string>();
            foreach (var entry in entries.EnumerateObject())
            {
                var status = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (!string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase))
                {
                    failing.Add(entry.Name);
                }
            }
            return failing.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>For processes without an HTTP health endpoint: is something accepting connections on the port?</summary>
    public static async Task<HealthResult> ProbePortAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return new HealthResult(true, $"port {port} open");
        }
        catch (Exception)
        {
            return new HealthResult(false, $"port {port} closed");
        }
    }

    /// <summary>Health of a recorded process: HTTP when it has a URL, otherwise the port. Tolerates the checks recorded at start.</summary>
    public static Task<HealthResult> ProbeAsync(RunningProcess p, CancellationToken ct = default) =>
        p.HealthUrl is null ? ProbePortAsync(p.Port, ct) : ProbeAsync(p.HealthUrl, acceptClientErrors: true, ct, p.Tolerated);

    /// <summary>Polls until healthy or the deadline passes. `isAlive` lets the wait stop early when the process died.</summary>
    public static async Task<HealthResult> WaitAsync(string? url, int port, TimeSpan timeout, Func<bool> isAlive, bool acceptClientErrors, CancellationToken ct, IReadOnlyDictionary<string, string>? tolerated = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        HealthResult last = new(false, "not started");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!isAlive())
            {
                return new HealthResult(false, "process exited");
            }
            last = url is null ? await ProbePortAsync(port, ct) : await ProbeAsync(url, acceptClientErrors, ct, tolerated ?? NoTolerance);
            if (last.Ok)
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return new HealthResult(false, $"gave up after {timeout.TotalSeconds:0}s (last: {last.Detail})");
    }

    /// <summary>Probes with one retry: libertine's first proxied request after start can time out while YARP warms up.</summary>
    public static async Task<HealthResult> ProbeWithRetryAsync(string url, CancellationToken ct)
    {
        var first = await ProbeAsync(url, ct: ct);
        if (first.Ok)
        {
            return first;
        }
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var second = await ProbeAsync(url, ct: ct);
        return second.Ok ? second with { Detail = second.Detail + " (after one retry)" } : second;
    }
}
