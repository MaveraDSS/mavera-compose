using System.Net;

namespace Devenv;

public sealed record HealthResult(bool Ok, string Detail);

public static class Health
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>One GET; 2xx/3xx is healthy. 4xx counts as "answering" for the frontend root (auth redirects) but not for health endpoints.</summary>
    public static async Task<HealthResult> ProbeAsync(string url, bool acceptClientErrors = false, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            var ok = code < 400 || (acceptClientErrors && code < 500);
            return new HealthResult(ok, $"HTTP {code}");
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

    /// <summary>Health of a recorded process: HTTP when it has a URL, otherwise the port.</summary>
    public static Task<HealthResult> ProbeAsync(RunningProcess p, CancellationToken ct = default) =>
        p.HealthUrl is null ? ProbePortAsync(p.Port, ct) : ProbeAsync(p.HealthUrl, acceptClientErrors: true, ct);

    /// <summary>Polls until healthy or the deadline passes. `isAlive` lets the wait stop early when the process died.</summary>
    public static async Task<HealthResult> WaitAsync(string? url, int port, TimeSpan timeout, Func<bool> isAlive, bool acceptClientErrors, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        HealthResult last = new(false, "not started");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!isAlive())
            {
                return new HealthResult(false, "process exited");
            }
            last = url is null ? await ProbePortAsync(port, ct) : await ProbeAsync(url, acceptClientErrors, ct);
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
