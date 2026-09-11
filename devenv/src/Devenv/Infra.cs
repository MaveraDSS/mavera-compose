namespace Devenv;

/// <summary>RabbitMQ, Redis and Jaeger (OTLP collector + UI) in docker, for services that run locally.</summary>
public static class Infra
{
    /// <summary>Host ports the infra compose file publishes, per compose service. Kept in sync with infra/docker-compose.yml by hand.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> PublishedPorts = new Dictionary<string, int[]>
    {
        ["rabbitmq"] = new[] { 5672, 15672 },
        ["redis"] = new[] { 6379 },
        ["jaeger"] = new[] { 4317, 4318, 16686 },
    };

    private static string[] Base(Workspace ws) => new[] { "compose", "-p", Workspace.InfraProjectName, "-f", ws.InfraComposeFile };

    public static async Task UpAsync(Workspace ws)
    {
        Console.WriteLine("infra: docker compose up -d (rabbitmq, redis, jaeger)");
        var (code, output) = await ProcessRunner.RunAsync("docker", Base(ws).Concat(new[] { "up", "-d", "--wait" }).ToList(), ws.Root, TimeSpan.FromMinutes(5), echo: true);
        if (code != 0)
        {
            throw new DevenvException($"docker compose up failed (exit {code}). Is Docker Desktop running? Use --no-infra to skip.\n{output}");
        }
    }

    public static async Task DownAsync(Workspace ws)
    {
        Console.WriteLine("infra: docker compose down");
        var (code, output) = await ProcessRunner.RunAsync("docker", Base(ws).Concat(new[] { "down" }).ToList(), ws.Root, TimeSpan.FromMinutes(2), echo: true);
        if (code != 0)
        {
            Console.Error.WriteLine($"warning: docker compose down exited {code}: {output}");
        }
    }

    /// <summary>Names of running containers in the devenv compose project, or null when docker is unavailable.</summary>
    public static async Task<IReadOnlyList<string>?> RunningAsync(Workspace ws)
    {
        var (code, output) = await ProcessRunner.RunAsync("docker", Base(ws).Concat(new[] { "ps", "--status", "running", "--format", "{{.Service}}" }).ToList(), ws.Root, TimeSpan.FromSeconds(30));
        if (code != 0)
        {
            return null;
        }
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
