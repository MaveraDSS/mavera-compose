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
    /// <summary>Start a service that sends mail; every message then goes to MAIL_TEST_ADDRESS.</summary>
    public bool AllowMail { get; init; }
    /// <summary>Branch to check out in repos devenv clones for --local services.</summary>
    public string? Branch { get; init; }
    /// <summary>setup: never prompt on the console for missing secrets.</summary>
    public bool NoPrompt { get; init; }
    /// <summary>setup: a secrets.json to import values from (a colleague's, or the one kept in 1Password as a document).</summary>
    public string? SecretsImport { get; init; }
    /// <summary>Hidden: this process is the background supervisor started by `up -d`.</summary>
    public bool Supervisor { get; init; }
    public List<string> RawArgs { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        string? command = null, env = null, repos = null, root = null, branch = null, secretsImport = null;
        bool detach = false, noInfra = false, skip = false, dry = false, supervisor = false, allowMigrations = false, allowMail = false, noPrompt = false;
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
                case "--allow-mail": allowMail = true; break;
                case "--branch": branch = Next(a); break;
                case "--no-prompt": noPrompt = true; break;
                case "--secrets": secretsImport = Next(a); break;
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
            AllowMail = allowMail, Branch = branch, NoPrompt = noPrompt, SecretsImport = secretsImport,
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
                "setup" => await Commands.SetupAsync(ws, options),
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
              setup     first time on a machine: clone the missing repos, install the frontend packages, fill
                        secrets.json (from --secrets <file>, then 1Password through `op` when signed in, then
                        by prompting for the required values); re-running only does what is still missing
              check     preflight only: tools, repos, VPN, ports, secrets
              render    write libertine's appsettings.Development.json, the frontend .env and the config of every
                        --local service (no start)
              up        clone missing repos, check, render, start infra (docker), libertine, the frontend and
                        --local services; Ctrl+C stops them
              status    show what is running and whether it answers
              down      stop everything `up` started, including the docker infra

            options
              --local <name>[,<name>]   services to run on this machine (see manifest.json): their config is rendered
                                        from the committed template, libertine routes point at localhost for them
              --allow-migrations        up: start a local service even though it would apply migration scripts the
                                        remote database has not seen
              --allow-mail              up: start a service that sends mail (notification-service); every message
                                        goes to MAIL_TEST_ADDRESS from secrets.json, never to real users
              --branch <name>           branch to check out in repos devenv has to clone for --local services
              --secrets <file>          setup: import values from another secrets.json (a colleague's, or the one
                                        kept as a document in 1Password); only fills what is still missing
              --no-prompt               setup: never ask for values on the console (CI, scripts); missing required
                                        secrets are listed instead
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
