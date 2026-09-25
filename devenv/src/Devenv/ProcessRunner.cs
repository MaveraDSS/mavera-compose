using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Devenv;

public sealed record ProcessSpec(
    string Name,
    string WorkingDir,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    int Port,
    string? HealthUrl,
    /// <summary>Second URL checked after HealthUrl; retried once because libertine's first proxied request can 504.</summary>
    string? EdgeHealthUrl,
    TimeSpan StartTimeout,
    /// <summary>Health entries (name → note) that may fail without failing the start; see ServiceSpec.OptionalHealthChecks.</summary>
    IReadOnlyDictionary<string, string>? Tolerated = null);

/// <summary>A child process with its output prefixed and mirrored to a log file.</summary>
public sealed class ChildProcess : IDisposable
{
    public ProcessSpec Spec { get; }
    public Process Process { get; }
    public string LogFile { get; }
    private readonly StreamWriter _log;
    private readonly bool _echo;
    private readonly object _gate = new();

    private ChildProcess(ProcessSpec spec, Process process, string logFile, bool echo)
    {
        Spec = spec;
        Process = process;
        LogFile = logFile;
        _echo = echo;
        _log = new StreamWriter(logFile, append: false) { AutoFlush = true };
    }

    public static ChildProcess Start(ProcessSpec spec, string logDir, bool echo)
    {
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, spec.Name + ".log");
        var psi = ProcessRunner.StartInfo(spec.Command[0], spec.Command.Skip(1).ToArray(), spec.WorkingDir, spec.Environment);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var child = new ChildProcess(spec, process, logFile, echo);
        process.OutputDataReceived += (_, e) => child.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => child.OnLine(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new DevenvException($"cannot start {spec.Name} ({string.Join(' ', spec.Command)}): {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            if (_echo)
            {
                Console.WriteLine($"[{Spec.Name}] {line}");
            }
        }
    }

    public bool HasExited
    {
        get { try { return Process.HasExited; } catch { return true; } }
    }

    public void Stop() => ProcessRunner.KillTree(Process);

    public void Dispose()
    {
        try { _log.Dispose(); } catch { /* ignore */ }
        Process.Dispose();
    }
}

public static class ProcessRunner
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Builds a start info that works without a shell; on Windows, npm-style .cmd shims go through cmd.exe.</summary>
    public static ProcessStartInfo StartInfo(string file, IReadOnlyList<string> args, string workingDir, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        if (IsWindows && NeedsCmdShim(file))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(file);
        }
        else
        {
            psi.FileName = file;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }
        return psi;
    }

    private static bool NeedsCmdShim(string file) =>
        file is "corepack" or "pnpm" or "npm" or "npx" or "yarn" or "node_modules/.bin/next";

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not stop pid {SafePid(p)}: {ex.Message}");
        }
    }

    public static bool KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            KillTree(p);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(); } catch { return "?"; }
    }

    /// <summary>Runs a command to completion, returning exit code and combined output.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, string workingDir, TimeSpan timeout, bool echo = false)
    {
        var psi = StartInfo(file, args, workingDir, new Dictionary<string, string>());
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = new Process { StartInfo = psi };
        var buffer = new System.Text.StringBuilder();
        var gate = new object();
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (gate) { buffer.AppendLine(line); }
            if (echo) Console.WriteLine($"  {line}");
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return (127, $"cannot start {file}: {ex.Message}");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            return (124, buffer + $"\n(timed out after {timeout.TotalSeconds:0}s)");
        }
        return (p.ExitCode, buffer.ToString().Trim());
    }
}
