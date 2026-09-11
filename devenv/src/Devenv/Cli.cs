namespace Devenv;

public sealed class CliOptions
{
    public string? Command { get; init; }
    public List<string> Local { get; } = new();
    public string? Environment { get; init; }
    public string? ReposRoot { get; init; }
    public string? Root { get; init; }
    public bool Detach { get; init; }
    public bool NoInfra { get; init; }
    public bool SkipPreflight { get; init; }
    public bool DryRun { get; init; }
    /// <summary>Start a local service even when its checkout holds migration scripts the remote database has not seen.</summary>
    public bool AllowMigrations { get; init; }
    /// <summary>Hidden: this process is the background supervisor started by `up -d`.</summary>
    public bool Supervisor { get; init; }
    public List<string> RawArgs { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        string? command = null, env = null, repos = null, root = null;
        bool detach = false, noInfra = false, skip = false, dry = false, supervisor = false, allowMigrations = false;
        var local = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next(string flag) => i + 1 < args.Length ? args[++i] : throw new DevenvException($"{flag} needs a value");
            switch (a)
            {
                case "--local": local.AddRange(Next(a).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--env": env = Next(a); break;
                case "--repos": repos = Next(a); break;
                case "--root": root = Next(a); break;
                case "-d" or "--detach": detach = true; break;
                case "--no-infra": noInfra = true; break;
                case "--skip-preflight": skip = true; break;
                case "--dry-run": dry = true; break;
                case "--allow-migrations": allowMigrations = true; break;
                case "--supervisor": supervisor = true; break;
                default:
                    if (a.StartsWith('-')) throw new DevenvException($"unknown option {a}");
                    if (command is not null) throw new DevenvException($"unexpected argument {a}");
                    command = a;
                    break;
            }
        }

        var o = new CliOptions
        {
            Command = command, Environment = env, ReposRoot = repos, Root = root, Detach = detach,
            NoInfra = noInfra, SkipPreflight = skip, DryRun = dry, Supervisor = supervisor, AllowMigrations = allowMigrations,
        };
        o.Local.AddRange(local);
        o.RawArgs.AddRange(args);
        return o;
    }
}

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (DevenvException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            PrintHelp();
            return 2;
        }

        if (options.Command is null or "help" or "--help" or "-h")
        {
            PrintHelp();
            return options.Command is null ? 1 : 0;
        }

        try
        {
            var ws = Workspace.Load(options);
            return options.Command switch
            {
                "up" => await Commands.UpAsync(ws, options),
                "down" => await Commands.DownAsync(ws),
                "status" => await Commands.StatusAsync(ws),
                "render" => Commands.Render(ws, options),
                "check" => await Commands.CheckAsync(ws),
                _ => Unknown(options.Command),
            };
        }
        catch (DevenvException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            // Console.Error may be redirected to devenv.log (background supervisor); an unhandled exception would bypass that.
            Console.Error.WriteLine($"unexpected error: {ex}");
            return 3;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        PrintHelp();
        return 2;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            devenv - run the DSS frontend and chosen services locally, the rest on a remote environment

            usage: devenv <command> [options]

            commands
              check     preflight only: tools, repos, VPN, ports, secrets
              render    write libertine's appsettings.Development.json, the frontend .env and the config of every
                        --local service (no start)
              up        check, render, start infra (docker), libertine and the frontend; Ctrl+C stops them
              status    show what is running and whether it answers
              down      stop everything `up` started, including the docker infra

            options
              --local <name>[,<name>]   services to run on this machine (see manifest.json): their config is rendered
                                        from the committed template, libertine routes point at localhost for them
              --allow-migrations        up: start a local service even though it would apply migration scripts the
                                        remote database has not seen (DbUp) or cannot be checked (EF Core)
              --env <name>              remote environment (default from devenv.local.json, else dev02)
              --repos <path>            folder holding the cloned repos (default: parent of mavera-compose)
              -d, --detach              up: leave everything running in the background and return
              --no-infra                up: do not start RabbitMQ/Redis/Jaeger in docker
              --skip-preflight          up: skip the checks (not recommended)
              --dry-run                 render: print to stdout instead of writing the files
              --root <path>             devenv folder (default: found from the current directory)
            """);
    }
}
