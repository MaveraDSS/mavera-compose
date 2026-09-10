using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Devenv;

public static class Commands
{
    // ---------------------------------------------------------------- check

    public static async Task<int> CheckAsync(Workspace ws)
    {
        Console.WriteLine($"devenv check  (environment {ws.Environment.Name}, repos in {ws.ReposRoot})");
        var results = await Preflight.RunAsync(ws, includePorts: true);
        Preflight.Print(results);
        var failed = results.Count(r => !r.Ok);
        Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
        return failed == 0 ? 0 : 1;
    }

    // --------------------------------------------------------------- render

    public static int Render(Workspace ws, CliOptions o)
    {
        var rendered = Renderers.All(ws);
        foreach (var r in rendered)
        {
            if (o.DryRun)
            {
                Console.WriteLine($"----- {r.Path}");
                Console.WriteLine(r.Content);
            }
            else
            {
                Renderers.Write(r);
                Console.WriteLine($"wrote {r.Path}");
            }
        }
        if (ws.LocalServices.Count > 0)
        {
            Console.WriteLine($"local services routed to localhost in libertine: {string.Join(", ", ws.LocalServices.Select(s => $"{s.Name} (:{s.Port})"))}");
        }
        return 0;
    }

    // ------------------------------------------------------------------- up

    public static async Task<int> UpAsync(Workspace ws, CliOptions o)
    {
        if (ws.LocalServices.Count > 0)
        {
            // Rendering libertine routes for local services works today; starting the services themselves
            // (their own config, migrations, broker safety) is DSS-5586.
            throw new DevenvException(
                $"starting local services is not implemented yet (DSS-5586): {string.Join(", ", ws.LocalServices.Select(s => s.Name))}. " +
                "Use `devenv render --local <name>` to point libertine at a service you start yourself, then `devenv up` without --local.");
        }

        if (File.Exists(ws.StateFile))
        {
            var existing = ReadState(ws);
            if (existing is not null && existing.Processes.Any(p => ProcessRunner.IsAlive(p.Pid)))
            {
                throw new DevenvException("something is already running (see `devenv status`); run `devenv down` first");
            }
            File.Delete(ws.StateFile);
        }

        if (o.Detach && !o.Supervisor)
        {
            return await DetachAsync(ws, o);
        }

        var echo = !o.Supervisor;
        if (o.Supervisor)
        {
            // Background mode: nobody is watching this console, and its stdio pipes close when the
            // parent `up -d` returns. Everything goes to devenv.log instead.
            Directory.CreateDirectory(ws.LogDir);
            var log = new StreamWriter(Path.Combine(ws.LogDir, "devenv.log"), append: false) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }
        if (!o.SkipPreflight)
        {
            Console.WriteLine("preflight");
            var results = await Preflight.RunAsync(ws, includePorts: true);
            Preflight.Print(results);
            if (results.Any(r => !r.Ok))
            {
                throw new DevenvException("preflight failed; fix the lines marked FAIL (or --skip-preflight if you know better)");
            }
        }

        Console.WriteLine("render");
        foreach (var r in Renderers.All(ws))
        {
            Renderers.Write(r);
            Console.WriteLine($"  wrote {r.Path}");
        }

        if (ws.Local.Infra)
        {
            await Infra.UpAsync(ws);
        }
        else
        {
            Console.WriteLine("infra: skipped (--no-infra or devenv.local.json); libertine's telemetry export will just fail quietly");
        }

        var specs = BuildProcessSpecs(ws);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
        using var sighup = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; if (!o.Supervisor) cts.Cancel(); });

        var children = new List<ChildProcess>();
        try
        {
            Console.WriteLine("start");
            foreach (var spec in specs)
            {
                Console.WriteLine($"  {spec.Name}: {string.Join(' ', spec.Command)}  (in {spec.WorkingDir})");
                children.Add(ChildProcess.Start(spec, ws.LogDir, echo));
            }
            WriteState(ws, children);

            var healthy = true;
            foreach (var child in children)
            {
                var spec = child.Spec;
                var result = await Health.WaitAsync(spec.HealthUrl, spec.StartTimeout, () => !child.HasExited, acceptClientErrors: spec.EdgeHealthUrl is null, cts.Token);
                Console.WriteLine($"  {spec.Name}: {spec.HealthUrl} -> {result.Detail}");
                healthy &= result.Ok;
                if (result.Ok && spec.EdgeHealthUrl is not null)
                {
                    var edge = await Health.ProbeWithRetryAsync(spec.EdgeHealthUrl, cts.Token);
                    Console.WriteLine($"  {spec.Name}: {spec.EdgeHealthUrl} -> {edge.Detail}{(edge.Ok ? "" : "  (remote side not reachable through libertine; check Zscaler and the log)")}");
                    healthy &= edge.Ok;
                }
            }

            if (!healthy)
            {
                Console.Error.WriteLine($"not everything came up; logs are in {ws.LogDir}");
                if (!o.Supervisor)
                {
                    return 1;
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"ready: frontend {ws.FrontendOrigin}  libertine {ws.GatewayOrigin}  remote {ws.Environment.Name}");
                Console.WriteLine(o.Supervisor ? $"logs in {ws.LogDir}; stop with `devenv down`" : "Ctrl+C stops the processes (docker infra stays up; `devenv down` stops that too)");
            }

            // Wait until cancelled or a child dies.
            while (!cts.IsCancellationRequested && children.All(c => !c.HasExited))
            {
                await Task.Delay(1000, CancellationToken.None);
            }
            var dead = children.FirstOrDefault(c => c.HasExited);
            if (dead is not null && !cts.IsCancellationRequested)
            {
                Console.Error.WriteLine($"{dead.Spec.Name} exited with code {SafeExitCode(dead.Process)}; stopping the rest (log: {dead.LogFile})");
                return 1;
            }
            return 0;
        }
        finally
        {
            Console.WriteLine("stopping");
            foreach (var child in Enumerable.Reverse(children))
            {
                child.Stop();
                child.Dispose();
            }
            if (File.Exists(ws.StateFile))
            {
                File.Delete(ws.StateFile);
            }
        }
    }

    private static async Task<int> DetachAsync(Workspace ws, CliOptions o)
    {
        // Run the preflight here so the user sees failures; the supervisor skips it.
        Console.WriteLine("preflight");
        var results = await Preflight.RunAsync(ws, includePorts: true);
        Preflight.Print(results);
        if (results.Any(r => !r.Ok))
        {
            throw new DevenvException("preflight failed; fix the lines marked FAIL");
        }

        var args = new List<string> { "up", "--supervisor", "--skip-preflight", "--root", ws.Root };
        args.AddRange(o.RawArgs.Where(a => a is not ("-d" or "--detach" or "up" or "--skip-preflight")).Where(a => a != "--root").ToList());
        // A pure Process.Start of ourselves: dotnet <devenv.dll> ... works both under `dotnet run` and as an installed tool.
        var dll = Path.Combine(AppContext.BaseDirectory, "devenv.dll");
        var command = new List<string> { "dotnet", dll };
        command.AddRange(args);
        var psi = ProcessRunner.StartInfo(command[0], command.Skip(1).ToList(), ws.Root, new Dictionary<string, string>());
        Directory.CreateDirectory(ws.LogDir);
        // Give the supervisor its own pipes so it does not inherit this terminal (a pipeline such as
        // `devenv up -d | tee` would otherwise never see end of file). The supervisor writes its own
        // console output to .state/logs/devenv.log; these pipes are drained and discarded.
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        var supervisor = Process.Start(psi) ?? throw new DevenvException("could not start the background supervisor");
        supervisor.OutputDataReceived += (_, _) => { };
        supervisor.ErrorDataReceived += (_, _) => { };
        supervisor.BeginOutputReadLine();
        supervisor.BeginErrorReadLine();
        Console.WriteLine($"supervisor started (pid {supervisor.Id}); waiting for health, logs in {ws.LogDir}");

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            if (supervisor.HasExited)
            {
                Console.Error.WriteLine($"supervisor exited with code {supervisor.ExitCode}; see {Path.Combine(ws.LogDir, "devenv.log")}");
                return 1;
            }
            var state = ReadState(ws);
            if (state is null || state.Processes.Count == 0)
            {
                continue;
            }
            var all = true;
            foreach (var p in state.Processes)
            {
                var r = await Health.ProbeAsync(p.HealthUrl, acceptClientErrors: true);
                all &= r.Ok;
            }
            if (all)
            {
                return await StatusAsync(ws);
            }
        }
        Console.Error.WriteLine("timed out waiting for the processes to answer; `devenv status` for details");
        return 1;
    }

    // ----------------------------------------------------------------- down

    public static async Task<int> DownAsync(Workspace ws)
    {
        var state = ReadState(ws);
        if (state is null)
        {
            Console.WriteLine("nothing recorded as running");
        }
        else
        {
            if (state.SupervisorPid != Environment.ProcessId && ProcessRunner.IsAlive(state.SupervisorPid))
            {
                Console.WriteLine($"stopping supervisor (pid {state.SupervisorPid})");
                ProcessRunner.KillTree(state.SupervisorPid);
            }
            foreach (var p in state.Processes)
            {
                var stopped = ProcessRunner.KillTree(p.Pid);
                Console.WriteLine($"  {p.Name} (pid {p.Pid}): {(stopped ? "stopped" : "was not running")}");
            }
            File.Delete(ws.StateFile);
        }
        if (ws.Local.Infra)
        {
            await Infra.DownAsync(ws);
        }
        return 0;
    }

    // --------------------------------------------------------------- status

    public static async Task<int> StatusAsync(Workspace ws)
    {
        var state = ReadState(ws);
        Console.WriteLine($"devenv status  (environment {ws.Environment.Name})");
        var exit = 0;
        if (state is null)
        {
            Console.WriteLine("  processes: none started by devenv");
            exit = 1;
        }
        else
        {
            Console.WriteLine($"  started {state.StartedAt:yyyy-MM-dd HH:mm}, supervisor pid {state.SupervisorPid} {(ProcessRunner.IsAlive(state.SupervisorPid) ? "(running)" : "(gone)")}");
            foreach (var p in state.Processes)
            {
                var alive = ProcessRunner.IsAlive(p.Pid);
                var health = alive ? await Health.ProbeAsync(p.HealthUrl, acceptClientErrors: true) : new HealthResult(false, "process gone");
                Console.WriteLine($"  {p.Name,-12} pid {p.Pid,-7} :{p.Port,-5} {(alive ? "running" : "stopped"),-8} {p.HealthUrl} -> {health.Detail}");
                if (!alive || !health.Ok) exit = 1;
            }
        }

        if (ws.Local.Infra)
        {
            var running = await Infra.RunningAsync(ws);
            Console.WriteLine(running is null
                ? "  infra: docker not available"
                : running.Count == 0 ? "  infra: not running" : $"  infra: {string.Join(", ", running)}");
        }
        return exit;
    }

    // -------------------------------------------------------------- helpers

    public static List<ProcessSpec> BuildProcessSpecs(Workspace ws)
    {
        var m = ws.Manifest;
        var gwRepo = ws.RepoPath(m.Gateway.Repo);
        var libertine = new ProcessSpec(
            "libertine",
            gwRepo,
            new[] { "dotnet", "run", "--project", m.Gateway.Project, "--no-launch-profile" },
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = ws.GatewayOrigin,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
            m.Gateway.Port,
            ws.GatewayOrigin + m.Gateway.HealthPath,
            ws.GatewayOrigin + m.Gateway.EdgeHealthPath,
            TimeSpan.FromMinutes(3));

        var frontend = new ProcessSpec(
            "frontend",
            Path.Combine(ws.RepoPath(m.Frontend.Repo), m.Frontend.WorkingDir),
            m.Frontend.Command,
            new Dictionary<string, string>
            {
                ["NODE_ENV"] = "development",
                ["NEXT_TELEMETRY_DISABLED"] = "1",
                ["PORT"] = m.Frontend.Port.ToString(),
            },
            m.Frontend.Port,
            ws.FrontendOrigin + m.Frontend.HealthPath,
            null,
            TimeSpan.FromMinutes(4));

        return new List<ProcessSpec> { libertine, frontend };
    }

    private static void WriteState(Workspace ws, IEnumerable<ChildProcess> children)
    {
        Directory.CreateDirectory(ws.StateDir);
        var state = new RunState
        {
            SupervisorPid = Environment.ProcessId,
            StartedAt = DateTimeOffset.Now,
            Environment = ws.Environment.Name,
            Infra = ws.Local.Infra,
            Processes = children.Select(c => new RunningProcess
            {
                Name = c.Spec.Name,
                Pid = c.Process.Id,
                Port = c.Spec.Port,
                HealthUrl = c.Spec.HealthUrl,
                LogFile = c.LogFile,
            }).ToList(),
        };
        File.WriteAllText(ws.StateFile, JsonSerializer.Serialize(state, Json.Options));
    }

    private static RunState? ReadState(Workspace ws)
    {
        if (!File.Exists(ws.StateFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(ws.StateFile), Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "?"; }
    }
}
