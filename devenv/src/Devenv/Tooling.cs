namespace Devenv;

/// <summary>`devenv logs`: the same log tail on every platform, for people and for the Claude Code skills.</summary>
public static class LogTail
{
    public static IReadOnlyList<string> Last(IReadOnlyList<string> lines, int count)
    {
        if (count <= 0 || lines.Count == 0) return Array.Empty<string>();
        return lines.Count <= count ? lines : lines.Skip(lines.Count - count).ToList();
    }

    /// <summary>Log files that exist in the log folder, by process name.</summary>
    public static IReadOnlyList<string> Available(string logDir) =>
        Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "*.log").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();
}

/// <summary>`devenv status --json`: everything the text status shows, as one object.</summary>
public sealed class StatusReport
{
    public string Environment { get; set; } = "";
    public string Database { get; set; } = "";
    public string FrontendUrl { get; set; } = "";
    public string LibertineUrl { get; set; } = "";
    public string LogDir { get; set; } = "";
    public SupervisorStatus? Supervisor { get; set; }
    public List<ProcessStatus> Processes { get; set; } = new();
    public bool InfraEnabled { get; set; }
    /// <summary>Running infra containers; null when docker is not available.</summary>
    public List<string>? Infra { get; set; }
    public List<LocalServiceStatus> LocalServices { get; set; } = new();
    public TestingSpec Testing { get; set; } = new();
    /// <summary>True when every recorded process is alive and answers.</summary>
    public bool Ok { get; set; }
}

public sealed class SupervisorStatus
{
    public int Pid { get; set; }
    public bool Alive { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class ProcessStatus
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public int Port { get; set; }
    public bool Alive { get; set; }
    public bool Healthy { get; set; }
    /// <summary>Health-check entries that fail but are tolerated (their secrets are blank); `detail` carries the note. Healthy stays true.</summary>
    public List<string> Degraded { get; set; } = new();
    public string Detail { get; set; } = "";
    public string? HealthUrl { get; set; }
    public string LogFile { get; set; } = "";
}

public sealed class LocalServiceStatus
{
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Branch { get; set; }
    public int Port { get; set; }
    public string? HealthUrl { get; set; }
}
