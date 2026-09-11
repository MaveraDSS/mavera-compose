using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Devenv;

public sealed record MigrationCheck(string Service, bool Verified, IReadOnlyList<string> Pending, string Detail)
{
    public bool Blocks => !Verified || Pending.Count > 0;
}

/// <summary>
/// Several services run database migrations at startup. Against the shared remote database that is only safe
/// when the checkout holds no script the database has not already seen. For DbUp services this compares the
/// .sql files in the migrations folder with the journal table; for EF Core the migration classes with the
/// __EFMigrationsHistory table.
/// </summary>
public static partial class MigrationPreflight
{
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex Identifier();

    public static async Task<MigrationCheck> CheckAsync(ServiceSpec service, string projectDir, string renderedConfigJson, CancellationToken ct)
    {
        switch (service.Migrations)
        {
            case null:
                return new MigrationCheck(service.Name, true, Array.Empty<string>(), "does not run migrations");
            case "dbup":
                return await CheckDbUpAsync(service, projectDir, renderedConfigJson, ct);
            case "efcore":
                return await CheckEfCoreAsync(service, projectDir, renderedConfigJson, ct);
            case "fluentmigrator":
                return await CheckFluentMigratorAsync(service, projectDir, renderedConfigJson, ct);
            default:
                throw new DevenvException($"{service.Name}: unknown migrations kind '{service.Migrations}' (expected dbup, efcore or fluentmigrator)");
        }
    }

    private static Task<MigrationCheck> CheckDbUpAsync(ServiceSpec service, string projectDir, string renderedConfigJson, CancellationToken ct)
    {
        var folder = Path.Combine(projectDir, service.MigrationsFolder ?? "_Migrations");
        if (!Directory.Exists(folder))
        {
            return Task.FromResult(new MigrationCheck(service.Name, true, Array.Empty<string>(), $"no migrations folder at {folder}"));
        }
        var scripts = Directory.GetFiles(folder, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        return CompareWithJournalAsync(service, folder, scripts, service.MigrationsJournal ?? "SchemaVersions", "ScriptName", renderedConfigJson, ct);
    }

    /// <summary>EF Core: every Migrations/&lt;timestamp&gt;_&lt;Name&gt;.cs (not the Designer or the model snapshot) is a MigrationId in __EFMigrationsHistory.</summary>
    private static Task<MigrationCheck> CheckEfCoreAsync(ServiceSpec service, string projectDir, string renderedConfigJson, CancellationToken ct)
    {
        var folder = Path.Combine(projectDir, service.MigrationsFolder ?? "Migrations");
        if (!Directory.Exists(folder))
        {
            return Task.FromResult(new MigrationCheck(service.Name, true, Array.Empty<string>(), $"no migrations folder at {folder}"));
        }
        var migrations = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && !n.EndsWith(".Designer", StringComparison.Ordinal) && !n.EndsWith("ModelSnapshot", StringComparison.Ordinal) && n.Length > 15 && n[14] == '_' && n[..14].All(char.IsDigit))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return CompareWithJournalAsync(service, folder, migrations, service.MigrationsJournal ?? "__EFMigrationsHistory", "MigrationId", renderedConfigJson, ct);
    }

    /// <summary>FluentMigrator: every [Migration(NNN)] class under the migrations folder is a Version in the VersionInfo table.</summary>
    private static Task<MigrationCheck> CheckFluentMigratorAsync(ServiceSpec service, string projectDir, string renderedConfigJson, CancellationToken ct)
    {
        var folder = Path.Combine(projectDir, service.MigrationsFolder ?? "Migrations");
        if (!Directory.Exists(folder))
        {
            return Task.FromResult(new MigrationCheck(service.Name, true, Array.Empty<string>(), $"no migrations folder at {folder}"));
        }
        var versions = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => MigrationAttribute().Matches(File.ReadAllText(f)).Select(m => $"{m.Groups[1].Value} ({Path.GetFileNameWithoutExtension(f)})"))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        return CompareWithJournalAsync(service, folder, versions, service.MigrationsJournal ?? "VersionInfo", "Version", renderedConfigJson, ct);
    }

    [GeneratedRegex(@"\[Migration\(\s*(\d+)")]
    private static partial Regex MigrationAttribute();

    private static async Task<MigrationCheck> CompareWithJournalAsync(ServiceSpec service, string folder, IReadOnlyList<string> scripts, string journal, string column, string renderedConfigJson, CancellationToken ct)
    {
        if (!Identifier().IsMatch(journal))
        {
            throw new DevenvException($"{service.Name}: migrationsJournal '{journal}' is not a plain table name");
        }
        var connectionString = ConnectionString(service, renderedConfigJson);

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [{column}] FROM dbo.[{journal}]";
            command.CommandTimeout = 30;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                applied.Add(name);
                applied.Add(Path.GetFileName(name.Replace('\\', '/')));
            }
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            return new MigrationCheck(service.Name, false, scripts, $"journal table dbo.{journal} does not exist in the remote database; every script would run");
        }
        catch (SqlException ex)
        {
            throw new DevenvException($"{service.Name}: cannot read migration journal dbo.{journal}: {ex.Message.Split('\n')[0]} (Zscaler connected? DB_PASSWORD correct?)");
        }
        catch (Exception ex) when (ex is not DevenvException)
        {
            throw new DevenvException($"{service.Name}: cannot connect to the {Path.GetFileName(folder)} journal database: {ex.Message.Split('\n')[0]}");
        }

        var pending = Pending(scripts, applied);
        return new MigrationCheck(service.Name, true, pending,
            pending.Count == 0
                ? $"{scripts.Count} scripts in {Path.GetFileName(folder)}, all in dbo.{journal}"
                : $"{pending.Count} of {scripts.Count} scripts not yet in dbo.{journal}");
    }

    /// <summary>Scripts in the checkout the journal does not know. Journal names may be full paths (DbUp file provider) or bare versions (FluentMigrator: entries look like "2310311400 (2310311400_Tables_Initial)").</summary>
    public static List<string> Pending(IEnumerable<string> scripts, IEnumerable<string> applied)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in applied)
        {
            known.Add(a);
            known.Add(Path.GetFileName(a.Replace('\\', '/')));
        }
        return scripts.Where(s => !known.Contains(s) && !known.Contains(Path.GetFileName(s)) && !known.Contains(s.Split(' ')[0])).ToList();
    }

    public sealed record Decision(bool Start, string Reason);

    /// <summary>The policy: an unverified or pending migration blocks unless the developer overrides, or the database is a local throwaway copy.</summary>
    public static Decision Decide(MigrationCheck check, bool allowMigrations, bool localDatabase)
    {
        if (!check.Blocks) return new Decision(true, check.Detail);
        if (localDatabase) return new Decision(true, $"{check.Detail}; allowed because the database is the local container (--db local)");
        if (allowMigrations) return new Decision(true, $"{check.Detail}; allowed by --allow-migrations, the shared database schema WILL change");
        return new Decision(false, $"{check.Detail}; refusing to change the shared database schema (rebase, or --allow-migrations)");
    }

    public static string ConnectionString(ServiceSpec service, string renderedConfigJson)
    {
        var name = service.ConnectionStringName ?? "MaveraContext";
        var root = JsonNode.Parse(renderedConfigJson)?.AsObject();
        var value = root?["ConnectionStrings"]?[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DevenvException($"{service.Name}: rendered config has no ConnectionStrings:{name}; set connectionStringName in the manifest");
        }
        return value;
    }
}
