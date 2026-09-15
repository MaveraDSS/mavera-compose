namespace Devenv;

public sealed record ClonePlan(string Repo, string Path, string Branch, string Reason);

/// <summary>Clone-on-demand: repos the current run needs but that are not next to mavera-compose yet.</summary>
public static class Repos
{
    /// <summary>What `up` would clone: the frontend, the gateway and each --local service whose folder is missing.</summary>
    public static List<ClonePlan> Plan(Manifest manifest, string reposRoot, IEnumerable<ServiceSpec> localServices, string? branchOverride)
    {
        var plan = new List<ClonePlan>();
        Add(plan, manifest.Frontend.Repo, reposRoot, manifest.Frontend.Branch, "frontend");
        Add(plan, manifest.Gateway.Repo, reposRoot, manifest.Gateway.Branch, "gateway");
        foreach (var s in localServices)
        {
            Add(plan, s.Repo, reposRoot, branchOverride ?? s.Branch, $"--local {s.Name}");
        }
        return plan;
    }

    private static void Add(List<ClonePlan> plan, string repo, string reposRoot, string branch, string reason)
    {
        var path = Path.Combine(reposRoot, repo);
        if (Directory.Exists(Path.Combine(path, ".git")) || plan.Any(p => p.Repo == repo))
        {
            return;
        }
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new DevenvException($"{path} exists but is not a git repository; move it away or clone {repo} there yourself");
        }
        plan.Add(new ClonePlan(repo, path, branch, reason));
    }

    public static async Task EnsureAsync(Workspace ws, IReadOnlyList<ClonePlan> plan, bool header = true)
    {
        if (plan.Count == 0) return;
        if (header) Console.WriteLine("clone");
        foreach (var p in plan)
        {
            var url = ws.Manifest.GitBase.TrimEnd('/') + "/" + p.Repo + ".git";
            Console.WriteLine($"  {p.Repo} ({p.Reason}): git clone --branch {p.Branch} {url}");
            var (code, output) = await ProcessRunner.RunAsync("git",
                new[] { "clone", "--quiet", "--branch", p.Branch, url, p.Path }, ws.ReposRoot, TimeSpan.FromMinutes(15), echo: true);
            if (code != 0)
            {
                throw new DevenvException($"cloning {p.Repo} failed (exit {code}): {output.Split('\n').LastOrDefault(l => l.Length > 0)}. Is the branch '{p.Branch}' right (manifest branch or --branch)? GitHub access set up?");
            }
        }
    }

    /// <summary>The git work tree that contains this folder, if any (a repos root inside a repository means mavera-compose was cloned in the wrong place).</summary>
    public static async Task<string?> EnclosingRepositoryAsync(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var (code, output) = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--show-toplevel" }, folder, TimeSpan.FromSeconds(20));
        return code == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary>Current branch of an existing clone, for the status lines; null when not a repo.</summary>
    public static async Task<string?> CurrentBranchAsync(string repoPath)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git"))) return null;
        var (code, output) = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repoPath, TimeSpan.FromSeconds(20));
        return code == 0 ? output.Trim() : null;
    }
}
