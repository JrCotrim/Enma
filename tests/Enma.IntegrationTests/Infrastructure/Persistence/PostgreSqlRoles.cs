using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

// Runs deploy/postgres/enma-roles.sql, the file every environment runs (D34),
// through a superuser connection: the file first, then one call on the same
// session. Passwords are bound parameters of that call, never SQL text.
internal static class PostgreSqlRoles
{
    private static readonly Lazy<string> ScriptPath = new(FindScriptPath);

    public static Task ApplyRolesAsync(
        string adminConnectionString,
        string migratorRole,
        string migratorPassword,
        string appRole,
        string appPassword,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            adminConnectionString,
            "SELECT pg_temp.enma_apply_roles(@migrator, @app, @migrator_password, @app_password)",
            new Dictionary<string, string>
            {
                ["migrator"] = migratorRole,
                ["app"] = appRole,
                ["migrator_password"] = migratorPassword,
                ["app_password"] = appPassword
            },
            cancellationToken);
    }

    public static Task ApplyGrantsAsync(
        string adminConnectionString,
        string migratorRole,
        string appRole,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            adminConnectionString,
            "SELECT pg_temp.enma_apply_grants(@migrator, @app)",
            new Dictionary<string, string>
            {
                ["migrator"] = migratorRole,
                ["app"] = appRole
            },
            cancellationToken);
    }

    public static Task<string> ReadScriptAsync(
        CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(ScriptPath.Value, cancellationToken);
    }

    private static async Task ExecuteAsync(
        string adminConnectionString,
        string call,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        string script = await ReadScriptAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var load = new NpgsqlCommand(script, connection))
        {
            await load.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var apply = new NpgsqlCommand(call, connection);
        foreach ((string name, string value) in parameters)
        {
            apply.Parameters.AddWithValue(name, value);
        }

        await apply.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FindScriptPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Enma.slnx")))
            {
                return Path.Combine(
                    directory.FullName, "deploy", "postgres", "enma-roles.sql");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Unable to locate the ENMA repository root for enma-roles.sql.");
    }
}
