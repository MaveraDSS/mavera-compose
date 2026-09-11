using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Devenv;

public sealed record PreflightResult(string Check, bool Ok, string Detail);

public static class Preflight
{
    public static async Task<List<PreflightResult>> RunAsync(Workspace ws, bool includePorts)
    {
        var results = new List<PreflightResult>();
        var m = ws.Manifest;

        results.Add(ToolCheck("dotnet", "dotnet", "--version"));
        results.Add(ToolCheck("node", "node", "--version"));
        results.Add(ToolCheck("corepack", "corepack", "--version"));
        if (ws.Local.Infra)
        {
            results.Add(ToolCheck("docker", "docker", "version", "--format", "{{.Server.Version}}"));
        }

        results.Add(RepoCheck("frontend repo", ws.RepoPath(m.Frontend.Repo), Path.Combine(m.Frontend.WorkingDir, "package.json")));
        results.Add(RepoCheck("libertine repo", ws.RepoPath(m.Gateway.Repo), m.Gateway.ConfigTemplate));
        var nodeModules = Path.Combine(ws.RepoPath(m.Frontend.Repo), "node_modules");
        results.Add(new PreflightResult("frontend packages", Directory.Exists(nodeModules),
            Directory.Exists(nodeModules) ? "node_modules present" : $"run `corepack pnpm install --frozen-lockfile` in {ws.RepoPath(m.Frontend.Repo)}"));
        foreach (var s in ws.LocalServices)
        {
            results.Add(s.Project is null
                ? new PreflightResult($"{s.Name} repo", false, "manifest has no project path for this service yet; fill in services[].project")
                : RepoCheck($"{s.Name} repo", ws.RepoPath(s.Repo), s.Project));
            if (!ws.Local.Infra)
            {
                results.Add(new PreflightResult($"{s.Name} infra", false, "a local service needs the local RabbitMQ/Redis; do not use --no-infra with --local"));
            }
            if (s.RequiresX64 && Workspace.IsArm64)
            {
                var x64 = ws.DotnetX64Path;
                results.Add(File.Exists(x64)
                    ? new PreflightResult($"{s.Name} x64 host", true, x64)
                    : new PreflightResult($"{s.Name} x64 host", false, $"{s.Name} carries x64-only assemblies and this is an arm64 machine; install the x64 .NET SDK side by side (expected at {x64}) or set dotnetX64 in devenv.local.json"));
            }
            if (s.SendsMail && !ws.Options.AllowMail)
            {
                results.Add(new PreflightResult($"{s.Name} mail", false, "sends mail; pass --allow-mail (every message then goes to MAIL_TEST_ADDRESS from secrets.json)"));
            }
        }

        results.Add(ws.HasSecretsFile
            ? new PreflightResult("secrets", true, ws.SecretsFile)
            : new PreflightResult("secrets", false, $"copy secrets.example.json to secrets.json and fill it (README)"));

        results.Add(await ConnectivityCheckAsync(ws.Environment));
        if (ws.LocalServices.Count > 0 && !ws.LocalDatabase)
        {
            results.Add(await SqlReachableAsync(ws.Environment));
        }

        if (includePorts && ws.Local.Infra)
        {
            // A RabbitMQ or Redis installed on the host (brew services, a Windows service) sits on the same
            // ports as the containers; the service would then talk to it with the wrong credentials.
            var running = await Infra.RunningAsync(ws) ?? Array.Empty<string>();
            var wanted = ws.LocalDatabase ? Infra.PublishedPorts.Concat(Infra.DatabasePorts) : Infra.PublishedPorts;
            foreach (var (service, ports) in wanted)
            {
                if (running.Contains(service, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var port in ports)
                {
                    var check = PortCheck($"infra {service}", port);
                    if (!check.Ok)
                    {
                        results.Add(check with { Detail = $"{port} is already served by another program (a host-installed {service}? `brew services list` / services.msc); stop it, the devenv container must own this port" });
                    }
                }
            }
        }

        if (includePorts)
        {
            results.Add(PortCheck("frontend port", m.Frontend.Port));
            results.Add(PortCheck("libertine port", m.Gateway.Port));
            foreach (var s in ws.LocalServices)
            {
                results.Add(PortCheck($"{s.Name} port", s.Port));
            }
        }

        return results;
    }

    public static void Print(IEnumerable<PreflightResult> results)
    {
        foreach (var r in results)
        {
            Console.WriteLine($"  {(r.Ok ? "ok  " : "FAIL")}  {r.Check,-20} {r.Detail}");
        }
    }

    private static PreflightResult ToolCheck(string name, string file, params string[] args)
    {
        try
        {
            var psi = ProcessRunner.StartInfo(file, args, Directory.GetCurrentDirectory(), new Dictionary<string, string>());
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            if (p.ExitCode == 0)
            {
                return new PreflightResult(name, true, output.Split('\n')[0]);
            }
            var reason = (stderr.Result.Trim().Split('\n').FirstOrDefault() ?? "").Trim();
            var hint = name == "docker" ? "is Docker Desktop running? (or use --no-infra)" : $"is {name} installed and on PATH?";
            return new PreflightResult(name, false, $"exit code {p.ExitCode}: {reason}. {hint}");
        }
        catch (Exception ex)
        {
            return new PreflightResult(name, false, $"not found ({ex.Message.Split('\n')[0]}); install it and open a new terminal");
        }
    }

    private static PreflightResult RepoCheck(string name, string repoPath, string mustContain)
    {
        if (!Directory.Exists(repoPath))
        {
            return new PreflightResult(name, false, $"not found at {repoPath}; clone it there or set reposRoot in devenv.local.json");
        }
        if (mustContain.Length > 0 && !File.Exists(Path.Combine(repoPath, mustContain)) && !Directory.Exists(Path.Combine(repoPath, mustContain)))
        {
            return new PreflightResult(name, false, $"{repoPath} exists but has no {mustContain}");
        }
        return new PreflightResult(name, true, repoPath);
    }

    private static async Task<PreflightResult> ConnectivityCheckAsync(EnvironmentSpec env)
    {
        var url = env.ConnectivityCheckUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new PreflightResult("remote env", true, "no connectivity check configured");
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync(url);
            return body.Contains("issuer", StringComparison.OrdinalIgnoreCase)
                ? new PreflightResult("remote env", true, $"{env.Name} reachable")
                : new PreflightResult("remote env", false, $"{url} answered but not with an OpenID discovery document");
        }
        catch (Exception ex)
        {
            return new PreflightResult("remote env", false, $"cannot reach {url}: {ex.InnerException?.Message ?? ex.Message}. Is Zscaler connected?");
        }
    }

    /// <summary>A port is "in use" when something accepts connections on it; binding tests give false negatives when the listener is on all interfaces.</summary>
    /// <summary>The remote SQL Server on 1433: the second thing (after the discovery document) that only answers with Zscaler connected.</summary>
    private static async Task<PreflightResult> SqlReachableAsync(EnvironmentSpec env)
    {
        if (string.IsNullOrWhiteSpace(env.Sql.Host))
        {
            return new PreflightResult("remote sql", true, "no SQL host configured");
        }
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(env.Sql.Host, 1433, cts.Token);
            return new PreflightResult("remote sql", true, $"{env.Sql.Host}:1433 reachable");
        }
        catch (Exception ex)
        {
            return new PreflightResult("remote sql", false, $"cannot reach {env.Sql.Host}:1433 ({ex.GetType().Name}); connect Zscaler, or use --db local");
        }
    }

    private static PreflightResult PortCheck(string name, int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                var connect = client.ConnectAsync(address, port);
                if (connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected)
                {
                    return new PreflightResult(name, false, $"{port} already in use (something running? try `devenv status` or `devenv down`)");
                }
            }
            catch (Exception)
            {
                // refused or unsupported address family: not in use on this address
            }
        }
        return new PreflightResult(name, true, $"{port} free");
    }
}
