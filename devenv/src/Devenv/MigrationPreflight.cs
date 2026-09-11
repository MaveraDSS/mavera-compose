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
/// .sql files in the migrations folder with the journal table; EF Core migrations cannot be checked this way
/// and are reported as unverified.
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
                return new MigrationCheck(service.Name, false, Array.Empty<string>(), "runs EF Core Migrate() at startup; devenv cannot compare that with the remote database yet");
            default:
                throw new DevenvException($"{service.Name}: unknown migrations kind '{service.Migrations}' (expected dbup or efcore)");
        }
    }

    private static async Task<MigrationCheck> CheckDbUpAsync(ServiceSpec service, string projectDir, string renderedConfigJson, CancellationToken ct)
    {
        var folder = Path.Combine(projectDir, service.MigrationsFolder ?? "_Migrations");
        if (!Directory.Exists(folder))
        {
            return new MigrationCheck(service.Name, true, Array.Empty<string>(), $"no migrations folder at {folder}");
        }
        var scripts = Directory.GetFiles(folder, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var journal = service.MigrationsJournal ?? "SchemaVersions";
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
            command.CommandText = $"SELECT ScriptName FROM dbo.[{journal}]";
            command.CommandTimeout = 30;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
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

        var pending = scripts.Where(s => !applied.Contains(s) && !applied.Contains(Path.GetFileName(s))).ToList();
        return new MigrationCheck(service.Name, true, pending,
            pending.Count == 0
                ? $"{scripts.Count} scripts in {Path.GetFileName(folder)}, all in dbo.{journal}"
                : $"{pending.Count} of {scripts.Count} scripts not yet in dbo.{journal}");
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
