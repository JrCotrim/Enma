using Npgsql;

namespace Enma.E2ETests.Infrastructure;

// Runs deploy/postgres/enma-roles.sql, the file every environment runs (D34),
// through a superuser connection: the file first, then one call on the same
// session. Passwords are bound parameters of that call, never SQL text.
internal static class PostgreSqlRoles
{
    public static async Task ApplyAsync(
        string adminConnectionString,
        string migratorRole,
        string migratorPassword,
        string appRole,
        string appPassword)
    {
        await ExecuteAsync(
            adminConnectionString,
            "SELECT pg_temp.enma_apply_roles(@migrator, @app, @migrator_password, @app_password)",
            new Dictionary<string, string>
            {
                ["migrator"] = migratorRole,
                ["app"] = appRole,
                ["migrator_password"] = migratorPassword,
                ["app_password"] = appPassword
            });
        await ExecuteAsync(
            adminConnectionString,
            "SELECT pg_temp.enma_apply_grants(@migrator, @app)",
            new Dictionary<string, string>
            {
                ["migrator"] = migratorRole,
                ["app"] = appRole
            });
    }

    private static async Task ExecuteAsync(
        string adminConnectionString,
        string call,
        IReadOnlyDictionary<string, string> parameters)
    {
        string script = await File.ReadAllTextAsync(Path.Combine(
            RepositoryPaths.FindRoot(), "deploy", "postgres", "enma-roles.sql"));
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using (var load = new NpgsqlCommand(script, connection))
        {
            await load.ExecuteNonQueryAsync();
        }

        await using var apply = new NpgsqlCommand(call, connection);
        foreach ((string name, string value) in parameters)
        {
            apply.Parameters.AddWithValue(name, value);
        }

        await apply.ExecuteNonQueryAsync();
    }
}
