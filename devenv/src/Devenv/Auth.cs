using System.Net.Http.Json;
using System.Text.Json;

namespace Devenv;

/// <summary>`devenv token`: a bearer token for API checks against the running stack, obtained the way the frontend's password login does it.</summary>
public static class Auth
{
    public const string TestUserKey = "TEST_USER";
    public const string TestPasswordKey = "TEST_PASSWORD";
    public const string RopcClientKey = "NEXT_PUBLIC_ROPC_CLIENT";
    public const string RopcSecretKey = "NEXT_PUBLIC_CLIENT_SECRET";

    /// <summary>The form the frontend's /api/login posts to /connect/token (IdentityServer resource-owner password grant).</summary>
    public static Dictionary<string, string> PasswordGrantForm(string clientId, string clientSecret, string username, string password) => new()
    {
        ["grant_type"] = "password",
        ["scope"] = "openid api1 api offline_access",
        ["client_id"] = clientId,
        ["client_secret"] = clientSecret,
        ["username"] = username,
        ["password"] = password,
        ["site"] = "localhost",
    };

    public sealed record Token(string AccessToken, int ExpiresIn, string Origin);

    /// <summary>Through local libertine when it answers (proves the edge), otherwise straight at the environment's public host.</summary>
    public static async Task<Token> GetTokenAsync(Workspace ws)
    {
        if (!ws.Environment.Frontend.TryGetValue(RopcClientKey, out var clientId) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new DevenvException($"environments/{ws.Environment.Name}.json has no frontend.{RopcClientKey}");
        }
        var form = PasswordGrantForm(clientId, ws.RequireSecret(RopcSecretKey), ws.RequireSecret(TestUserKey), ws.RequireSecret(TestPasswordKey));

        var viaLibertine = (await Health.ProbePortAsync(ws.Manifest.Gateway.Port)).Ok;
        var origin = viaLibertine ? ws.GatewayOrigin : ws.Environment.PublicBaseUrl.TrimEnd('/');

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(origin + "/connect/token", new FormUrlEncodedContent(form));
        }
        catch (HttpRequestException ex)
        {
            throw new DevenvException($"cannot reach {origin}/connect/token: {ex.InnerException?.Message ?? ex.Message}. Is the stack up (`devenv status`) or Zscaler connected?");
        }
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new DevenvException($"{origin}/connect/token answered HTTP {(int)response.StatusCode}: {ErrorOf(body)}. Check {TestUserKey}/{TestPasswordKey} in secrets.json (the user must have a password login, not Okta) and {RopcSecretKey}.");
        }
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new DevenvException($"{origin}/connect/token answered without an access_token");
        }
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 0;
        return new Token(token, expires, origin);
    }

    /// <summary>The identity server's error code and description, never the request we sent.</summary>
    private static string ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return string.Join(": ", new[] { error, description }.Where(s => !string.IsNullOrEmpty(s)));
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
