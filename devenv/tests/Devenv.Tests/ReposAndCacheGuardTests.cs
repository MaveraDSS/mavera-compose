using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class ReposAndCacheGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devenv-tests-" + Guid.NewGuid().ToString("N"));

    public ReposAndCacheGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void FakeClone(string repo)
    {
        Directory.CreateDirectory(Path.Combine(_root, repo, ".git"));
    }

    [Fact]
    public void PlanClonesOnlyWhatIsMissing()
    {
        var m = Fixture.Manifest();
        FakeClone(m.Frontend.Repo);
        var evaluation = m.FindService("evaluation-service")!;
        var plan = Repos.Plan(m, _root, new[] { evaluation }, branchOverride: null);
        Assert.Equal(new[] { m.Gateway.Repo, evaluation.Repo }, plan.Select(p => p.Repo));
        Assert.Equal(m.Gateway.Branch, plan[0].Branch);
        Assert.Equal("develop", plan[1].Branch);
        Assert.Equal(Path.Combine(_root, evaluation.Repo), plan[1].Path);
    }

    [Fact]
    public void PlanUsesTheBranchOverrideForLocalServicesOnly()
    {
        var m = Fixture.Manifest();
        var evaluation = m.FindService("evaluation-service")!;
        var plan = Repos.Plan(m, _root, new[] { evaluation }, branchOverride: "feature/DSS-1234");
        Assert.Equal("feature/DSS-1234", plan.Single(p => p.Repo == evaluation.Repo).Branch);
        Assert.Equal(m.Gateway.Branch, plan.Single(p => p.Repo == m.Gateway.Repo).Branch);
        Assert.Equal(m.Frontend.Branch, plan.Single(p => p.Repo == m.Frontend.Repo).Branch);
    }

    [Fact]
    public void PlanIsEmptyWhenEverythingIsCloned()
    {
        var m = Fixture.Manifest();
        FakeClone(m.Frontend.Repo);
        FakeClone(m.Gateway.Repo);
        Assert.Empty(Repos.Plan(m, _root, Array.Empty<ServiceSpec>(), null));
    }

    [Fact]
    public void PlanRefusesANonRepoFolderInTheWay()
    {
        var m = Fixture.Manifest();
        Directory.CreateDirectory(Path.Combine(_root, m.Frontend.Repo));
        File.WriteAllText(Path.Combine(_root, m.Frontend.Repo, "leftover.txt"), "x");
        var ex = Assert.Throws<DevenvException>(() => Repos.Plan(m, _root, Array.Empty<ServiceSpec>(), null));
        Assert.Contains("not a git repository", ex.Message);
    }

    [Fact]
    public void AClusterRedisHostIsRefused()
    {
        var m = Fixture.Manifest();
        var input = new ServiceRenderInput(m.FindService("evaluation-service")!,
            """{ "DistributedCacheProjectSettings": { "CacheService": "redis.ktest.mavera.com", "Port": "6379" } }""",
            new Dictionary<string, string>(), m, Fixture.Dev02(), Array.Empty<ServiceSpec>(), "test");
        var ex = Assert.Throws<DevenvException>(() => ServiceConfigRenderer.Render(input));
        Assert.Contains("CacheService", ex.Message);
    }
}
