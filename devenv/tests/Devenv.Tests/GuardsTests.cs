using Devenv;
using Xunit;

namespace Devenv.Tests;

public class GuardsTests
{
    private static string Mail(string environment, string testAddress) =>
        $$"""{ "MailSettings": { "MailEnvironment": "{{environment}}", "TestingEmailAddress": "{{testAddress}}" } }""";

    [Fact]
    public void MailRefusesWithoutTheFlag()
    {
        var problems = Guards.Mail(Mail("Development", "me@example.com"), "notification-service", allowMail: false);
        Assert.Single(problems);
        Assert.Contains("--allow-mail", problems[0]);
    }

    [Fact]
    public void MailAllowsWithFlagTestAddressAndNonProductionEnvironment()
    {
        Assert.Empty(Guards.Mail(Mail("Development", "me@example.com"), "notification-service", allowMail: true));
        Assert.Empty(Guards.Mail(Mail("Test", "me@example.com"), "notification-service", allowMail: true));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Demo")]
    [InlineData("production")]
    public void MailRefusesEnvironmentsThatReachRealUsersEvenWithTheFlag(string environment)
    {
        var problems = Guards.Mail(Mail(environment, "me@example.com"), "notification-service", allowMail: true);
        Assert.Contains(problems, p => p.Contains("real users"));
    }

    [Fact]
    public void MailRefusesABlankTestAddress()
    {
        var problems = Guards.Mail(Mail("Development", ""), "notification-service", allowMail: true);
        Assert.Contains(problems, p => p.Contains("MAIL_TEST_ADDRESS"));
    }

    [Fact]
    public void MailIgnoresServicesWithoutAMailSection()
    {
        Assert.Empty(Guards.Mail("""{ "Logging": {} }""", "evaluation-service", allowMail: false));
    }

    [Fact]
    public void MigrationPendingMatchesJournalByNameFileNameOrVersion()
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0001_a.sql", @"C:\build\_Migrations\0002_b.sql", "2310311400" };
        var scripts = new[] { "0001_a.sql", "0002_b.sql", "0003_c.sql", "2310311400 (2310311400_Tables_Initial)", "2311011200 (2311011200_Tables_Seed)" };
        var pending = MigrationPreflight.Pending(scripts, applied);
        Assert.Equal(new[] { "0003_c.sql", "2311011200 (2311011200_Tables_Seed)" }, pending);
    }

    [Fact]
    public void MigrationDecisionRefusesPendingScriptsAgainstTheRemoteDatabase()
    {
        var check = new MigrationCheck("evaluation-service", Verified: true, new[] { "0360_new.sql" }, "1 of 360 pending");
        var d = MigrationPreflight.Decide(check, allowMigrations: false, localDatabase: false);
        Assert.False(d.Start);
        Assert.Contains("--allow-migrations", d.Reason);
    }

    [Fact]
    public void MigrationDecisionHonoursTheOverrideAndTheLocalDatabase()
    {
        var check = new MigrationCheck("evaluation-service", Verified: true, new[] { "0360_new.sql" }, "1 of 360 pending");
        Assert.True(MigrationPreflight.Decide(check, allowMigrations: true, localDatabase: false).Start);
        Assert.True(MigrationPreflight.Decide(check, allowMigrations: false, localDatabase: true).Start);
    }

    [Fact]
    public void MigrationDecisionRefusesUnverifiedChecks()
    {
        var check = new MigrationCheck("caregivers", Verified: false, Array.Empty<string>(), "journal table missing");
        Assert.False(MigrationPreflight.Decide(check, allowMigrations: false, localDatabase: false).Start);
        Assert.True(MigrationPreflight.Decide(check, allowMigrations: true, localDatabase: false).Start);
    }

    [Fact]
    public void MigrationDecisionStartsWhenNothingIsPending()
    {
        var check = new MigrationCheck("user-service", Verified: true, Array.Empty<string>(), "3 scripts, all in journal");
        Assert.True(MigrationPreflight.Decide(check, allowMigrations: false, localDatabase: false).Start);
    }

    [Fact]
    public void LocalDatabaseWarningNamesTheServicesAndTheEnvironment()
    {
        var text = Guards.LocalDatabaseWarning("dev02", new[] { "evaluation-service" });
        Assert.Contains("evaluation-service", text);
        Assert.Contains("dev02", text);
        Assert.Contains("CONTAINER", text);
    }
}
