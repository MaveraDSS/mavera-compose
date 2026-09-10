namespace Devenv;

/// <summary>Turns a workspace into the two generated files and knows where they go.</summary>
public static class Renderers
{
    public sealed record Rendered(string Path, string Content);

    public static Rendered Libertine(Workspace ws)
    {
        var gw = ws.Manifest.Gateway;
        var repo = ws.RepoPath(gw.Repo);
        var templatePath = Path.Combine(repo, gw.ConfigTemplate);
        if (!File.Exists(templatePath))
        {
            throw new DevenvException($"libertine template not found: {templatePath} (is {gw.Repo} cloned next to mavera-compose, or set reposRoot in devenv.local.json?)");
        }
        var json = LibertineConfigRenderer.Render(new LibertineRenderInput(
            File.ReadAllText(templatePath),
            ws.Manifest,
            ws.Environment,
            ws.LocalServices,
            ws.RequireSecret(gw.OktaInternalSecretKey),
            ws.DeveloperName,
            ws.Local.OtlpEndpoint,
            ws.FrontendOrigin));
        return new Rendered(Path.Combine(repo, gw.ConfigOutput), json);
    }

    public static Rendered Frontend(Workspace ws)
    {
        var fe = ws.Manifest.Frontend;
        if (!ws.HasSecretsFile)
        {
            throw new DevenvException($"secrets.json not found at {ws.SecretsFile}; copy secrets.example.json to secrets.json and fill it from 1Password (see README)");
        }
        var content = FrontendEnvRenderer.Render(new FrontendRenderInput(
            ws.Manifest, ws.Environment, ws.Secrets, ws.GatewayOrigin, ws.FrontendOrigin));
        return new Rendered(Path.Combine(ws.RepoPath(fe.Repo), fe.WorkingDir, fe.EnvFile), content);
    }

    public static IReadOnlyList<Rendered> All(Workspace ws) => new[] { Libertine(ws), Frontend(ws) };

    public static void Write(Rendered r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(r.Path)!);
        File.WriteAllText(r.Path, r.Content);
    }
}
