using Devenv;
using Xunit;

namespace Devenv.Tests;

public class PlaceholderTableTests
{
    private const string Sample = """
        x-dotnet-env: &dotnet-env
          ASPNETCORE_ENVIRONMENT: Production

        x-placeholders: &placeholders
          # a comment line
          Log_Level: ${LOG_LEVEL:-Information}
          Log_IncludeScopes: "false"
          Tracing_Connection_String: http://otel-collector:4317   # MUST be an absolute URI
          ServiceSettings_EnvURL: ${GATEWAY_HOST:?GATEWAY_HOST is not set}
          ServiceSettings_ClientUrl: ${PUBLIC_SCHEME:-http}://${GATEWAY_HOST:?GATEWAY_HOST is not set}
          MessageBroker_VirtualHost: "/"
          Okta_Domain: ${OKTA_DOMAIN:-}
          RedisPort: "6379"

        services:
          sqlserver:
            image: mcr.microsoft.com/mssql/server:2022-latest
        """;

    [Fact]
    public void ParsesOnlyThePlaceholderBlock()
    {
        var values = PlaceholderTable.ParseComposeDefaults(Sample);
        Assert.Equal(8, values.Count);
        Assert.False(values.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.False(values.ContainsKey("image"));
    }

    [Fact]
    public void ResolvesComposeVariableSyntax()
    {
        var values = PlaceholderTable.ParseComposeDefaults(Sample);
        Assert.Equal("Information", values["Log_Level"]);
        Assert.Equal("", values["ServiceSettings_EnvURL"]);
        Assert.Equal("http://", values["ServiceSettings_ClientUrl"]);
        Assert.Equal("", values["Okta_Domain"]);
    }

    [Fact]
    public void StripsQuotesAndComments()
    {
        var values = PlaceholderTable.ParseComposeDefaults(Sample);
        Assert.Equal("false", values["Log_IncludeScopes"]);
        Assert.Equal("/", values["MessageBroker_VirtualHost"]);
        Assert.Equal("6379", values["RedisPort"]);
        Assert.Equal("http://otel-collector:4317", values["Tracing_Connection_String"]);
    }

    [Fact]
    public void TheRealComposeFileParsesToTheFullVocabulary()
    {
        var values = PlaceholderTable.ParseComposeDefaults(Fixture.Read("docker-compose.yml"));
        Assert.True(values.Count > 150, $"only {values.Count} placeholders parsed");
        Assert.Equal("vera-dev02", values["ConnectionStrings_DB_Name"]);
        Assert.Equal("mavera-user-service", values["ServiceSettings_Services_UserService"]);
        Assert.Equal("4000", values["DefaultMaxLengthEvaluationAttribute"]);
    }

    [Fact]
    public void LayersOverrideInOrderAndSecretsWin()
    {
        var defaults = new Dictionary<string, string> { ["A"] = "compose", ["B"] = "compose", ["C"] = "compose", ["D"] = "" };
        var env = new EnvironmentSpec { Placeholders = new() { ["A"] = "env", ["B"] = "env" } };
        var spec = new PlaceholderSpec
        {
            Local = new() { ["B"] = "local" },
            Secrets = new() { ["C"] = new SecretPlaceholder { Key = "C_SECRET", Required = true }, ["E"] = new SecretPlaceholder { Key = "E_SECRET" } },
        };
        var secrets = new Dictionary<string, string> { ["C_SECRET"] = "s3cret" };
        var used = new HashSet<string> { "A", "B", "C", "D", "E" };

        var r = PlaceholderTable.Resolve(defaults, env, spec, new Dictionary<string, string>(), new Dictionary<string, string>(), secrets, used);

        Assert.Equal("env", r.Values["A"]);
        Assert.Equal("local", r.Values["B"]);
        Assert.Equal("s3cret", r.Values["C"]);
        Assert.Empty(r.MissingRequiredSecrets);
        Assert.Equal(new[] { "D", "E" }, r.Blank);
    }

    [Fact]
    public void ServiceOverridesBeatLocalButNotComputed()
    {
        var spec = new PlaceholderSpec { Local = new() { ["CacheType"] = "redis", ["X"] = "local" } };
        var service = new Dictionary<string, string> { ["CacheType"] = "inmemory", ["X"] = "service" };
        var computed = new Dictionary<string, string> { ["X"] = "computed" };
        var r = PlaceholderTable.Resolve(new Dictionary<string, string>(), new EnvironmentSpec(), spec, service, computed, new Dictionary<string, string>(), new HashSet<string> { "CacheType", "X" });
        Assert.Equal("inmemory", r.Values["CacheType"]);
        Assert.Equal("computed", r.Values["X"]);
    }

    [Fact]
    public void RequiredSecretMissingIsReportedOnlyWhenTheTemplateUsesIt()
    {
        var spec = new PlaceholderSpec { Secrets = new() { ["P"] = new SecretPlaceholder { Key = "K", Required = true } } };
        var none = new Dictionary<string, string>();

        var used = PlaceholderTable.Resolve(none, new EnvironmentSpec(), spec, none, none, none, new HashSet<string> { "P" });
        Assert.Single(used.MissingRequiredSecrets);
        Assert.Contains("K", used.MissingRequiredSecrets[0]);

        var unused = PlaceholderTable.Resolve(none, new EnvironmentSpec(), spec, none, none, none, new HashSet<string>());
        Assert.Empty(unused.MissingRequiredSecrets);
    }

    [Fact]
    public void ABlankOptionalSecretDoesNotClobberALowerLayer()
    {
        var env = new EnvironmentSpec { Placeholders = new() { ["P"] = "from-env" } };
        var spec = new PlaceholderSpec { Secrets = new() { ["P"] = new SecretPlaceholder { Key = "K" } } };
        var none = new Dictionary<string, string>();
        var r = PlaceholderTable.Resolve(none, env, spec, none, none, none, new HashSet<string> { "P" });
        Assert.Equal("from-env", r.Values["P"]);
        Assert.Empty(r.Blank);
    }

    [Fact]
    public void TokensInFindsEveryDistinctPlaceholder()
    {
        var tokens = PlaceholderTable.TokensIn("\"a\": \"$X\", \"b\": \"http://$Y.svc/$X\", \"c\": \"$$Z\"");
        Assert.Equal(new[] { "X", "Y", "Z" }, tokens.OrderBy(t => t));
    }
}
