using System.Text.Json.Nodes;

namespace Devenv;

/// <summary>Start-time policies that are about what a local service could do to shared systems.</summary>
public static class Guards
{
    private static readonly string[] RealRecipientEnvironments = { "Production", "Demo" };

    /// <summary>
    /// notification-service sends every mail to MailSettings:TestingEmailAddress unless MailEnvironment is
    /// Production or Demo. A local copy therefore needs: a non-production environment, a test address, and
    /// the developer's explicit --allow-mail.
    /// </summary>
    public static List<string> Mail(string renderedConfigJson, string service, bool allowMail)
    {
        var problems = new List<string>();
        var root = JsonNode.Parse(renderedConfigJson)?.AsObject();
        var mail = root?["MailSettings"]?.AsObject();
        if (mail is null)
        {
            return problems; // no mail section, nothing to guard
        }
        var environment = mail["MailEnvironment"]?.GetValue<string>() ?? "";
        var testAddress = mail["TestingEmailAddress"]?.GetValue<string>() ?? "";
        if (RealRecipientEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{service}: MailSettings:MailEnvironment is '{environment}', which sends mail to real users; a local service must use Development or Test (manifest placeholders.local)");
        }
        if (string.IsNullOrWhiteSpace(testAddress))
        {
            problems.Add($"{service}: MailSettings:TestingEmailAddress is blank; set MAIL_TEST_ADDRESS in secrets.json to your own address, every mail the local service sends goes there");
        }
        if (!allowMail)
        {
            problems.Add($"{service}: sends mail (to {(string.IsNullOrWhiteSpace(testAddress) ? "the test address" : testAddress)} only); pass --allow-mail to start it");
        }
        return problems;
    }

    /// <summary>The warning printed when --db local is combined with anything that still lives on the remote environment.</summary>
    public static string LocalDatabaseWarning(string environment, IEnumerable<string> localServices)
    {
        var locals = string.Join(", ", localServices);
        return string.Join('\n', new[]
        {
            "==================================================================================",
            $"  --db local: {(locals.Length == 0 ? "no local service" : locals)} use(s) the SQL Server CONTAINER (restored from the 2024 dev02 backups),",
            $"  while libertine, the frontend login and every remote service still work on {environment}'s live data.",
            "  Ids, users and organisations will not match between the two. Use this mode to work on schema and",
            "  data of the local services only; anything that crosses the boundary will look broken.",
            "==================================================================================",
        });
    }
}
