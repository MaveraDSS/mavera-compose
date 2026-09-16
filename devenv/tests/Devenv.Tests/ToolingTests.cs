using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class ToolingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devenv-tooling-tests-" + Guid.NewGuid().ToString("N"));

    public ToolingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ------------------------------------------------------------------ logs

    [Fact]
    public void LogTailReturnsTheLastLinesInOrder()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToList();
        Assert.Equal(new[] { "line 8", "line 9", "line 10" }, LogTail.Last(lines, 3));
        Assert.Equal(lines, LogTail.Last(lines, 100));
        Assert.Empty(LogTail.Last(lines, 0));
        Assert.Empty(LogTail.Last(new List<string>(), 5));
    }

    [Fact]
    public void LogTailListsAvailableLogsByProcessName()
    {
        File.WriteAllText(Path.Combine(_root, "libertine.log"), "x");
        File.WriteAllText(Path.Combine(_root, "frontend.log"), "x");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        Assert.Equal(new[] { "frontend", "libertine" }, LogTail.Available(_root));
        Assert.Empty(LogTail.Available(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void LogsOptionsParse()
    {
        var o = CliOptions.Parse(new[] { "logs", "evaluation-service", "--tail", "40" });
        Assert.Equal("logs", o.Command);
        Assert.Equal("evaluation-service", o.Target);
        Assert.Equal(40, o.Tail);
        Assert.Equal(100, CliOptions.Parse(new[] { "logs" }).Tail);
        Assert.Throws<DevenvException>(() => CliOptions.Parse(new[] { "logs", "a", "b" }));
        Assert.Throws<DevenvException>(() => CliOptions.Parse(new[] { "logs", "a", "--tail", "zero" }));
    }

    // ----------------------------------------------------------------- token

    [Fact]
    public void PasswordGrantFormMatchesTheFrontendsLoginRoute()
    {
        var form = Auth.PasswordGrantForm("ropc_client", "s3cret", "tester@example.com", "pw");
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("openid api1 api offline_access", form["scope"]);
        Assert.Equal("ropc_client", form["client_id"]);
        Assert.Equal("s3cret", form["client_secret"]);
        Assert.Equal("tester@example.com", form["username"]);
        Assert.Equal("pw", form["password"]);
        Assert.Equal("localhost", form["site"]);
        Assert.Equal(7, form.Count);
    }

    [Fact]
    public void TheEnvironmentCarriesTheRopcClientAndATestingSection()
    {
        var env = Fixture.Dev02();
        Assert.Equal("ropc_client", env.Frontend[Auth.RopcClientKey]);
        Assert.True(env.Testing.Configured);
        Assert.Equal("E2E Tests Organisation", env.Testing.OrganizationName);
        Assert.True(Guid.TryParse(env.Testing.OrganizationId, out _));
    }

    [Fact]
    public void TheExampleSecretsCarryTheTestUserKeys()
    {
        var keys = SecretsSetup.ParseExample(Fixture.Read("secrets.example.json")).Select(e => e.Key).ToHashSet();
        Assert.Contains(Auth.TestUserKey, keys);
        Assert.Contains(Auth.TestPasswordKey, keys);
    }

    // ----------------------------------------------------------- claude.md

    [Fact]
    public void ReposRootClaudeMdNamesTheReposAndTheSkillsAndIsNeverOverwritten()
    {
        var m = Fixture.Manifest();
        var text = ReposRootClaudeMd.Render(m);
        Assert.Contains("`verisk-nordics-frontend`", text);
        Assert.Contains("`mavera-libertine`", text);
        Assert.Contains("`mavera-evaluation-service`", text);
        Assert.Contains("/dss:ticket", text);
        Assert.Contains("read its `CLAUDE.md`", text);

        Assert.True(ReposRootClaudeMd.WriteIfMissing(m, _root));
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "mine");
        Assert.False(ReposRootClaudeMd.WriteIfMissing(m, _root));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_root, "CLAUDE.md")));
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void StatusReportSerialisesWithStableCamelCaseKeys()
    {
        var report = new StatusReport
        {
            Environment = "dev02",
            Database = "sql.example",
            Supervisor = new SupervisorStatus { Pid = 1, Alive = true, StartedAt = DateTimeOffset.UnixEpoch },
            Processes = { new ProcessStatus { Name = "libertine", Pid = 2, Port = 5151, Alive = true, Healthy = true, Detail = "HTTP 200", HealthUrl = "http://localhost:5151/x", LogFile = "l.log" } },
            InfraEnabled = true,
            Infra = new List<string> { "rabbitmq" },
            Ok = true,
        };
        var json = JsonSerializer.Serialize(report, Json.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var key in new[] { "environment", "database", "frontendUrl", "libertineUrl", "logDir", "supervisor", "processes", "infraEnabled", "infra", "localServices", "testing", "ok" })
        {
            Assert.True(root.TryGetProperty(key, out _), $"missing {key}");
        }
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("libertine", root.GetProperty("processes")[0].GetProperty("name").GetString());
        Assert.True(root.GetProperty("processes")[0].GetProperty("healthy").GetBoolean());
        Assert.False(root.GetProperty("testing").GetProperty("configured").GetBoolean());
    }

    [Fact]
    public void StatusJsonOptionParses()
    {
        Assert.True(CliOptions.Parse(new[] { "status", "--json" }).Json);
        Assert.False(CliOptions.Parse(new[] { "status" }).Json);
    }
}
