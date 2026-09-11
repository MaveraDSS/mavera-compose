using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class ServiceConfigRendererTests
{
    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["DB_PASSWORD"] = "db-pass",
        ["OKTA_INTERNAL_SECRET"] = "okta-secret",
        ["CRYPTO_IV"] = "iv",
        ["CRYPTO_KEY"] = "key",
    };

    private static ServiceRenderInput Input(string template, string serviceName = "evaluation-service", params string[] localServices)
    {
        var manifest = Fixture.Manifest();
        var env = Fixture.Dev02();
        var service = manifest.FindService(serviceName)!;
        var locals = localServices.Select(n => manifest.FindService(n)!).ToList();
        var composeDefaults = PlaceholderTable.ParseComposeDefaults(Fixture.Read("docker-compose.yml"));
        var computed = new Dictionary<string, string> { ["ServiceSettings_ENV"] = "Local-DEV-Test", ["Tracing_Connection_String"] = "http://localhost:4317" };
        var resolution = PlaceholderTable.Resolve(composeDefaults, env, manifest.Placeholders, service.Placeholders, computed, Secrets, PlaceholderTable.TokensIn(template));
        Assert.Empty(resolution.MissingRequiredSecrets);
        return new ServiceRenderInput(service, template, resolution.Values, manifest, env, locals, "test");
    }

    private static JsonElement Render(string template, string serviceName = "evaluation-service", params string[] locals) =>
        JsonDocument.Parse(ServiceConfigRenderer.Render(Input(template, serviceName, locals)).Json).RootElement;

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (var p in path) e = e.GetProperty(p);
        return e.GetString()!;
    }

    [Fact]
    public void EvaluationServiceTemplateRendersWithoutPlaceholdersOrClusterHosts()
    {
        var result = ServiceConfigRenderer.Render(Input(Fixture.Read("evaluation.appsettings.json")));
        Assert.DoesNotContain("svc.cluster.local", result.Json);
        foreach (var token in PlaceholderTable.TokensIn(Fixture.Read("evaluation.appsettings.json")))
        {
            Assert.DoesNotContain("$" + token, result.Json);
        }
        Assert.Contains("\"_generated\": \"test\"", result.Json);
    }

    [Fact]
    public void RemoteDatabaseAndAuthComeFromTheEnvironment()
    {
        var root = Render(Fixture.Read("evaluation.appsettings.json"));
        var cs = Str(root, "ConnectionStrings", "MaveraContext");
        Assert.Contains("Server=tcp:mssql-internal.test.mavera.claims.verisk.com,1433", cs);
        Assert.Contains("Initial Catalog=vera-dev02", cs);
        Assert.Contains("User Id=libertine", cs);
        Assert.Contains("Password=db-pass", cs);
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com", Str(root, "SecuritySettings", "Authority"));
        Assert.Equal("https://sso.int.verisk.com/oauth2/aus2ta01bgqyH6hHq0h8", Str(root, "SecuritySettings", "OktaAuthority"));
        Assert.Equal("vnordics-dev02aws-dss", Str(root, "SecuritySettings", "OktaAudience"));
        Assert.Equal("0oa2ta051fvbwMCpr0h8", Str(root, "InternalServices", "clientId"));
        Assert.Equal("okta-secret", Str(root, "InternalServices", "secret"));
    }

    [Fact]
    public void LocalInfraReplacesClusterInfra()
    {
        var root = Render(Fixture.Read("evaluation.appsettings.json"));
        Assert.Equal("localhost", Str(root, "Messaging", "Host"));
        Assert.Equal("mavera", Str(root, "Messaging", "Username"));
        Assert.Equal("redis", Str(root, "DistributedCacheProjectSettings", "CacheType"));
        Assert.Equal("localhost", Str(root, "DistributedCacheProjectSettings", "CacheService"));
        Assert.Equal("http://localhost:4317", Str(root, "OpenTelemetry", "OTLP", "Endpoint"));
        Assert.Equal("Local-DEV-Test", Str(root, "serviceName", "serviceEnv"));
        Assert.Equal("Information", Str(root, "Logging", "LogLevel", "Default"));
    }

    [Fact]
    public void OtherServicesPointAtTheInternalIngressWithTheTemplatePath()
    {
        var root = Render(Fixture.Read("evaluation.appsettings.json"));
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/UserService", Str(root, "ServiceSettings", "UserService"));
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/DocumentService", Str(root, "ServiceSettings", "DocumentService"));
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/caregivers", Str(root, "ServiceSettings", "CaregiverService"));
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com", Str(root, "ServiceSettings", "TranslationService"));
        // Named by bare hostname in the template: gets the service's remotePath.
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/MedicalAdvisorNetwork", Str(root, "ServiceSettings", "MedicalAdvisorNetworkService"));
    }

    [Fact]
    public void AnotherLocalServiceIsReachedOnLocalhost()
    {
        var root = Render(Fixture.Read("evaluation.appsettings.json"), "evaluation-service", "user-service", "medical-advisor-network");
        Assert.Equal("http://localhost:5202/Vera/UserService", Str(root, "ServiceSettings", "UserService"));
        Assert.Equal("http://localhost:5204/Vera/MedicalAdvisorNetwork", Str(root, "ServiceSettings", "MedicalAdvisorNetworkService"));
        Assert.StartsWith("https://dev02-internal", Str(root, "ServiceSettings", "DocumentService"));
    }

    [Fact]
    public void ThisServiceSelfReferenceAlsoBecomesLocalhost()
    {
        var root = Render(Fixture.Read("evaluation.appsettings.json"), "evaluation-service", "evaluation-service");
        Assert.Equal("http://localhost:5201/Vera/EvaluationService", Str(root, "ServiceSettings", "EvaluationService"));
    }

    [Fact]
    public void BareIdentityServerUrlBecomesTheAuthority()
    {
        const string template = """{ "InternalServices": { "Authority": "http://mavera-identity-server", "Audience": "internalapi" } }""";
        var root = Render(template);
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com", Str(root, "InternalServices", "Authority"));
    }

    [Fact]
    public void CommentsInTheTemplateAreTolerated()
    {
        const string template = """
            {
              // line comment
              "A": "$Log_Level", /* block */
              "B": { "CacheType": "$CacheType" //{inmemory, redis}
              }
            }
            """;
        var root = Render(template);
        Assert.Equal("Information", Str(root, "A"));
        Assert.Equal("redis", Str(root, "B", "CacheType"));
    }

    [Fact]
    public void SecretValuesAreJsonEscaped()
    {
        var manifest = Fixture.Manifest();
        var values = new Dictionary<string, string> { ["ConnectionStrings_DB_Pass"] = "pa\"ss\\word" };
        var input = new ServiceRenderInput(manifest.FindService("evaluation-service")!, """{ "ConnectionStrings": { "MaveraContext": "Server=tcp:mssql-internal.test.mavera.claims.verisk.com;Password=$ConnectionStrings_DB_Pass;" } }""",
            values, manifest, Fixture.Dev02(), Array.Empty<ServiceSpec>(), "test");
        var root = JsonDocument.Parse(ServiceConfigRenderer.Render(input).Json).RootElement;
        Assert.Contains("Password=pa\"ss\\word;", Str(root, "ConnectionStrings", "MaveraContext"));
    }

    [Fact]
    public void AClusterBrokerHostIsRefused()
    {
        const string template = """{ "Messaging": { "Host": "rabbitmq.dev02.internal", "Username": "x" } }""";
        var ex = Assert.Throws<DevenvException>(() => Render(template));
        Assert.Contains("Messaging:Host", ex.Message);
    }

    [Fact]
    public void AnUnresolvedPlaceholderIsRefused()
    {
        const string template = """{ "X": "$No_Such_Placeholder_Anywhere" }""";
        var ex = Assert.Throws<DevenvException>(() => Render(template));
        Assert.Contains("No_Such_Placeholder_Anywhere", ex.Message);
    }

    [Fact]
    public void AConnectionStringToAnotherHostIsAWarning()
    {
        var manifest = Fixture.Manifest();
        var input = new ServiceRenderInput(manifest.FindService("evaluation-service")!, """{ "ConnectionStrings": { "Other": "Server=tcp:prod-sql,1433;Initial Catalog=x;" } }""",
            new Dictionary<string, string>(), manifest, Fixture.Dev02(), Array.Empty<ServiceSpec>(), "test");
        var result = ServiceConfigRenderer.Render(input);
        Assert.Contains(result.Warnings, w => w.Contains("prod-sql"));
    }
}
