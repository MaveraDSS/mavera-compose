using Devenv;
using Xunit;

namespace Devenv.Tests;

public class FrontendEnvRendererTests
{
    private static Dictionary<string, string> Secrets(bool complete = true)
    {
        var s = new Dictionary<string, string>
        {
            ["NEXTAUTH_SECRET"] = "nextauth",
            ["NEXT_PUBLIC_CLIENT_SECRET"] = "ropc",
            ["NEXT_PUBLIC_PDFTRON_LICENSE_KEY"] = "pdftron key with spaces",
            ["NEXT_PUBLIC_ASSETS_BASE"] = "https://assets.example",
            ["OKTA_CLIENT_SECRET"] = "okta",
        };
        if (!complete) s.Remove("OKTA_CLIENT_SECRET");
        return s;
    }

    private static string Render(Dictionary<string, string> secrets) =>
        FrontendEnvRenderer.Render(new FrontendRenderInput(Fixture.Manifest(), Fixture.Dev02(), secrets, "http://localhost:5151", "http://localhost:3002"));

    private static Dictionary<string, string> Parse(string env) =>
        env.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1].TrimEnd('\r'));

    [Fact]
    public void PointsTheFrontendAtLocalLibertineWithEnvironmentOktaValues()
    {
        var vars = Parse(Render(Secrets()));
        Assert.Equal("http://localhost:5151", vars["NEXT_PUBLIC_VERA_BASE_PATH"]);
        Assert.Equal("http://localhost:3002", vars["NEXTAUTH_URL"]);
        Assert.Equal("https://sso.int.verisk.com/oauth2/aus2ta01bgqyH6hHq0h8", vars["OKTA_ISSUER"]);
        Assert.Equal("0oa2ta051dgJQg8jd0h8", vars["OKTA_CLIENT_ID"]);
        Assert.Equal("ropc_client", vars["NEXT_PUBLIC_ROPC_CLIENT"]);
        Assert.Equal("stage", vars["NEXT_PUBLIC_SMB_SETTINGS_SHARE"]);
    }

    [Fact]
    public void CopiesSecretsAndQuotesValuesWithSpaces()
    {
        var vars = Parse(Render(Secrets()));
        Assert.Equal("okta", vars["OKTA_CLIENT_SECRET"]);
        Assert.Equal("\"pdftron key with spaces\"", vars["NEXT_PUBLIC_PDFTRON_LICENSE_KEY"]);
        Assert.Equal("", vars["AZURE_AD_TENANT_ID"]); // optional, blank when absent
    }

    [Fact]
    public void FailsLoudlyWhenARequiredSecretIsMissing()
    {
        var ex = Assert.Throws<DevenvException>(() => Render(Secrets(complete: false)));
        Assert.Contains("OKTA_CLIENT_SECRET", ex.Message);
    }

    [Fact]
    public void FailsWhenARequiredSecretIsBlank()
    {
        var secrets = Secrets();
        secrets["NEXTAUTH_SECRET"] = "  ";
        var ex = Assert.Throws<DevenvException>(() => Render(secrets));
        Assert.Contains("NEXTAUTH_SECRET", ex.Message);
    }
}
