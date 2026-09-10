using System.Text.Json;
using Devenv;
using Xunit;

namespace Devenv.Tests;

public class LibertineConfigRendererTests
{
    private const string Secret = "not-a-real-secret";

    private static LibertineRenderInput Input(params string[] localServices)
    {
        var manifest = Fixture.Manifest();
        var locals = localServices.Select(n => manifest.FindService(n) ?? throw new Xunit.Sdk.XunitException($"no service {n}")).ToList();
        return new LibertineRenderInput(
            Fixture.LibertineTemplate(), manifest, Fixture.Dev02(), locals, Secret, "Test", "http://localhost:4317", "http://localhost:3002");
    }

    private static JsonElement Render(params string[] localServices) =>
        Fixture.Parse(LibertineConfigRenderer.Render(Input(localServices))).RootElement;

    private static string ClusterAddress(JsonElement root, string clusterId)
    {
        var destinations = root.GetProperty("ReverseProxy").GetProperty("Clusters").GetProperty(clusterId).GetProperty("Destinations");
        return destinations.EnumerateObject().First().Value.GetProperty("Address").GetString()!;
    }

    private static JsonElement Routes(JsonElement root) => root.GetProperty("ReverseProxy").GetProperty("Routes");

    [Fact]
    public void NoPlaceholderSurvives()
    {
        var json = LibertineConfigRenderer.Render(Input());
        Assert.DoesNotContain("$", json);
        Assert.DoesNotContain("svc.cluster.local", json);
    }

    [Fact]
    public void EveryTemplateClusterIsRenderedAndPointsAtTheRemoteEnvironment()
    {
        var template = Fixture.Parse(Fixture.LibertineTemplate()).RootElement.GetProperty("ReverseProxy").GetProperty("Clusters");
        var rendered = Render();
        foreach (var cluster in template.EnumerateObject())
        {
            var address = ClusterAddress(rendered, cluster.Name);
            Assert.StartsWith("https://dev02", address);
            Assert.EndsWith("/", address);
        }
    }

    [Fact]
    public void ClusterPathsFromTheTemplateAreKept()
    {
        var rendered = Render();
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/UserService/", ClusterAddress(rendered, "mavera-user-service"));
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/", ClusterAddress(rendered, "mavera-evaluation-service"));
    }

    [Fact]
    public void PublicClustersUseThePublicHost()
    {
        var rendered = Render();
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com/", ClusterAddress(rendered, "mavera-document-service"));
    }

    [Fact]
    public void DestinationKeysFromTheTemplateAreKept()
    {
        // YARP does not care, but keeping "destination" vs "Route" makes diffs against the template readable.
        var rendered = Render();
        var userService = rendered.GetProperty("ReverseProxy").GetProperty("Clusters").GetProperty("mavera-user-service").GetProperty("Destinations");
        Assert.True(userService.TryGetProperty("destination", out _));
    }

    [Fact]
    public void ServiceSettingsFollowIngressAndPathOverrides()
    {
        var services = Render().GetProperty("ServiceSettings").GetProperty("Services").EnumerateArray()
            .ToDictionary(e => e.GetProperty("Name").GetString()!, e => e);
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com/Vera/", services["Monolith"].GetProperty("Url").GetString());
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com/", services["Monolith"].GetProperty("Domain").GetString());
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com/Vera/DocumentService/", services["DocumentManagement"].GetProperty("Url").GetString());
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/MedicalAdvisorNetwork/", services["MedicalAdvisorNetwork"].GetProperty("Url").GetString());
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/Vera/", services["UserService"].GetProperty("Url").GetString());
        Assert.Equal("https://dev02-internal.test.mavera.claims.verisk.com/", services["EvaluationService"].GetProperty("Domain").GetString());
    }

    [Fact]
    public void EdgeRoutesForwardToThePublicClusterAndNothingCatchesRoot()
    {
        var rendered = Render();
        var routes = Routes(rendered);
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com/", ClusterAddress(rendered, "dev02-public"));
        foreach (var (route, path) in new[]
                 {
                     ("edge-vera", "Vera/{**catch-all}"),
                     ("edge-connect", "connect/{**catch-all}"),
                     ("edge-well-known", ".well-known/{**catch-all}"),
                     ("edge-reflectionservice", "ReflectionService/{**catch-all}"),
                     ("edge-identity-client", "identity-client/{**catch-all}"),
                 })
        {
            var r = routes.GetProperty(route);
            Assert.Equal("dev02-public", r.GetProperty("ClusterId").GetString());
            Assert.Equal(path, r.GetProperty("Match").GetProperty("Path").GetString());
        }
        Assert.DoesNotContain(routes.EnumerateObject(), r => r.Value.GetProperty("Match").GetProperty("Path").GetString() == "{**catch-all}");
    }

    [Fact]
    public void LocalServiceGetsLocalhostClusterRouteAndServiceSettings()
    {
        var rendered = Render("evaluation-service");
        Assert.Equal("http://localhost:5201/", ClusterAddress(rendered, "mavera-evaluation-service"));
        Assert.Equal("http://localhost:5201/", ClusterAddress(rendered, "local-evaluation-service"));

        var route = Routes(rendered).GetProperty("edge-vera-evaluationservice");
        Assert.Equal("local-evaluation-service", route.GetProperty("ClusterId").GetString());
        Assert.Equal("Vera/EvaluationService/{**catch-all}", route.GetProperty("Match").GetProperty("Path").GetString());
        // The general Vera route still goes to the remote environment.
        Assert.Equal("dev02-public", Routes(rendered).GetProperty("edge-vera").GetProperty("ClusterId").GetString());

        var evaluation = rendered.GetProperty("ServiceSettings").GetProperty("Services").EnumerateArray()
            .Single(e => e.GetProperty("Name").GetString() == "EvaluationService");
        Assert.Equal("http://localhost:5201/Vera/", evaluation.GetProperty("Url").GetString());
        Assert.Equal("http://localhost:5201/", evaluation.GetProperty("Domain").GetString());
        // Other services are untouched.
        Assert.StartsWith("https://dev02-internal", ClusterAddress(rendered, "mavera-user-service"));
    }

    [Fact]
    public void LocalServiceThatOwnsAnEdgePrefixReplacesThatEdgeRoute()
    {
        var rendered = Render("identity-client");
        var route = Routes(rendered).GetProperty("edge-identity-client");
        Assert.Equal("local-identity-client", route.GetProperty("ClusterId").GetString());
        Assert.Equal("identity-client/{**catch-all}", route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal("http://localhost:5207/", ClusterAddress(rendered, "identity-client-service"));
        Assert.False(Routes(rendered).TryGetProperty("edge-local-identity-client", out _));
    }

    [Fact]
    public void AuthTelemetryAndLocalDevSectionsAreFilledFromEnvironment()
    {
        var rendered = Render();
        var security = rendered.GetProperty("SecuritySettings");
        Assert.Equal("https://dev02.test.mavera.claims.verisk.com", security.GetProperty("Authority").GetString());
        Assert.Equal("https://sso.int.verisk.com/oauth2/aus2ta01bgqyH6hHq0h8", security.GetProperty("OktaAuthority").GetString());
        Assert.Equal("vnordics-dev02aws-dss", security.GetProperty("OktaAudience").GetString());

        var internalSettings = rendered.GetProperty("InternalServiceSettings");
        Assert.Equal("0oa2ta051fvbwMCpr0h8", internalSettings.GetProperty("clientId").GetString());
        Assert.Equal(Secret, internalSettings.GetProperty("secret").GetString());
        Assert.Equal("internalapi", internalSettings.GetProperty("scope").GetString());

        Assert.Equal("Local-DEV-Test", rendered.GetProperty("serviceName").GetProperty("serviceEnv").GetString());
        Assert.Equal("http://localhost:4317", rendered.GetProperty("OpenTelemetry").GetProperty("OTLP").GetProperty("Endpoint").GetString());

        var localDev = rendered.GetProperty("LocalDev");
        Assert.Equal("http://localhost:3002", localDev.GetProperty("CorsOrigins")[0].GetString());
        Assert.Equal(524288000, localDev.GetProperty("MaxRequestBodySize").GetInt64());
    }

    [Theory]
    [InlineData("http://$ServiceSettings_Services_UserService.svc.cluster.local/Vera/UserService/", "Vera/UserService/")]
    [InlineData("https://$ServiceSettings_EnvURL/Vera/", "Vera/")]
    [InlineData("http://$ServiceSettings_Services_Idp2/", "")]
    [InlineData("$ServiceSettings_Services_MedicalAdvisorsNetwork", "")]
    [InlineData("https://mavera.ngrok.pdfsearch.app.ngrok.pizza/", "")]
    public void ExtractPathKeepsOnlyWhatFollowsTheHost(string committed, string expected)
    {
        Assert.Equal(expected, LibertineConfigRenderer.ExtractPath(committed));
    }
}
