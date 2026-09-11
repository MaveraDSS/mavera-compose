using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class ReposAndLocalDbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devenv-tests-" + Guid.NewGuid().ToString("N"));

    public ReposAndLocalDbTests() => Directory.CreateDirectory(_root);

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
    public void LocalDatabaseOverridesWinOverSecretsAndClearMissingPassword()
    {
        var m = Fixture.Manifest();
        var used = new HashSet<string> { "ConnectionStrings_DB_Server", "ConnectionStrings_DB_User", "ConnectionStrings_DB_Pass" };
        var noSecrets = new Dictionary<string, string>();
        var remote = PlaceholderTable.Resolve(new Dictionary<string, string>(), Fixture.Dev02(), m.Placeholders, new Dictionary<string, string>(), new Dictionary<string, string>(), noSecrets, used);
        Assert.Single(remote.MissingRequiredSecrets); // DB_PASSWORD is required against the remote database

        var local = PlaceholderTable.Resolve(new Dictionary<string, string>(), Fixture.Dev02(), m.Placeholders, new Dictionary<string, string>(), new Dictionary<string, string>(), noSecrets, used, m.Placeholders.LocalDatabase);
        Assert.Empty(local.MissingRequiredSecrets);
        Assert.Equal("localhost", local.Values["ConnectionStrings_DB_Server"]);
        Assert.Equal("sa", local.Values["ConnectionStrings_DB_User"]);
        Assert.Equal("Mavera_L0cal_Dev", local.Values["ConnectionStrings_DB_Pass"]);
    }

    [Fact]
    public void RenderedConnectionStringPointsAtTheContainerWithLocalDatabase()
    {
        var m = Fixture.Manifest();
        var template = Fixture.Read("evaluation.appsettings.json");
        var compose = PlaceholderTable.ParseComposeDefaults(Fixture.Read("docker-compose.yml"));
        var computed = new Dictionary<string, string> { ["ServiceSettings_ENV"] = "x", ["Tracing_Connection_String"] = "http://localhost:4317" };
        var secrets = new Dictionary<string, string> { ["OKTA_INTERNAL_SECRET"] = "s" };
        var r = PlaceholderTable.Resolve(compose, Fixture.Dev02(), m.Placeholders, new Dictionary<string, string>(), computed, secrets, PlaceholderTable.TokensIn(template), m.Placeholders.LocalDatabase);
        var result = ServiceConfigRenderer.Render(new ServiceRenderInput(m.FindService("evaluation-service")!, template, r.Values, m, Fixture.Dev02(), Array.Empty<ServiceSpec>(), "test", SqlHost: "localhost"));
        var cs = JsonDocument.Parse(result.Json).RootElement.GetProperty("ConnectionStrings").GetProperty("MaveraContext").GetString()!;
        Assert.Contains("Server=tcp:localhost,1433", cs);
        Assert.Contains("User Id=sa", cs);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("ConnectionStrings"));
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
