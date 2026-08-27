using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuditLogMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260824170123_AddNotifications";
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        27,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrateAsync_FromPreviousSchema_PreservesDataAndCreatesAuditSchema()
    {
        await MigrateAsync(PreviousMigration);
        var organization = new Organization(
            "Existing Audit Tenant",
            "existing-audit-tenant",
            CreatedAt);
        var user = new User(
            "Existing Audit Actor",
            "existing.audit.actor@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(organization, user, membership);
            await seedContext.SaveChangesAsync();
        }

        string[] tablesBefore = await GetPublicTablesAsync();
        Assert.Equal(0, await CountDuplicateMembershipIdsAsync());
        Assert.Null(await GetConstraintColumnsAsync(
            "organization_memberships",
            "ak_organization_memberships_organization_id_id_user_id"));

        await MigrateAsync();

        Assert.Equal(
            tablesBefore.Append("audit_logs").Order(StringComparer.Ordinal),
            await GetPublicTablesAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            organization.Id,
            (await dbContext.Organizations.SingleAsync()).Id);
        Assert.Equal(user.Id, (await dbContext.Users.SingleAsync()).Id);
        Assert.Equal(
            membership.Id,
            (await dbContext.OrganizationMemberships.SingleAsync()).Id);
        Assert.False(dbContext.Database.HasPendingModelChanges());

        await AssertColumnsAsync();
        await AssertConstraintsAsync();
        await AssertIndexesAsync();
        await AssertAppendOnlyTriggerAsync();
    }

    private async Task AssertColumnsAsync()
    {
        const string Query =
            """
            SELECT
                column_name,
                data_type,
                is_nullable,
                character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'audit_logs'
            ORDER BY ordinal_position
            """;
        var actual = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            actual.Add(
                $"{reader.GetString(0)}|{reader.GetString(1)}|" +
                $"{reader.GetString(2)}|" +
                $"{(reader.IsDBNull(3) ? "" : reader.GetInt32(3))}");
        }

        Assert.Equal(
            [
                "id|uuid|NO|",
                "organization_id|uuid|NO|",
                "actor_user_id|uuid|NO|",
                "actor_membership_id|uuid|NO|",
                "actor_role_at_occurrence|integer|NO|",
                "event_type|integer|NO|",
                "entity_type|integer|NO|",
                "entity_id|uuid|NO|",
                "occurred_at|timestamp with time zone|NO|",
                "trace_id|character varying|YES|32",
                "details|jsonb|YES|"
            ],
            actual);
    }

    private async Task AssertConstraintsAsync()
    {
        string[] expectedAuditConstraints =
        [
            "ck_audit_logs_actor_role_at_occurrence",
            "ck_audit_logs_details_contract",
            "ck_audit_logs_details_size",
            "ck_audit_logs_entity_type",
            "ck_audit_logs_event_entity_type",
            "ck_audit_logs_event_type",
            "ck_audit_logs_trace_id",
            "fk_audit_logs_memberships_org_membership_user_id",
            "fk_audit_logs_organizations_organization_id",
            "pk_audit_logs"
        ];

        Assert.Equal(
            expectedAuditConstraints,
            await GetConstraintNamesAsync("audit_logs"));
        Assert.Equal(
            "organization_id,id,user_id",
            await GetConstraintColumnsAsync(
                "organization_memberships",
                "ak_organization_memberships_organization_id_id_user_id"));
        Assert.Equal(
            "organization_id,actor_membership_id,actor_user_id=>" +
            "organization_id,id,user_id|RESTRICT",
            await GetForeignKeyShapeAsync(
                "fk_audit_logs_memberships_org_membership_user_id"));
        Assert.Equal(
            "organization_id=>id|RESTRICT",
            await GetForeignKeyShapeAsync(
                "fk_audit_logs_organizations_organization_id"));
    }

    private async Task AssertIndexesAsync()
    {
        Dictionary<string, string> indexes = await GetIndexesAsync();
        string[] expectedNames =
        [
            "ix_audit_logs_org_actor_membership_id_actor_user_id",
            "ix_audit_logs_org_actor_user_id_occurred_at_id",
            "ix_audit_logs_org_entity_type_entity_id_occurred_at_id",
            "ix_audit_logs_org_event_type_occurred_at_id",
            "ix_audit_logs_organization_id_occurred_at_id",
            "pk_audit_logs"
        ];

        Assert.Equal(expectedNames, indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(
            "(organization_id, occurred_at DESC, id DESC)",
            indexes["ix_audit_logs_organization_id_occurred_at_id"],
            StringComparison.Ordinal);
        Assert.Contains(
            "(organization_id, entity_type, entity_id, occurred_at DESC, id DESC)",
            indexes["ix_audit_logs_org_entity_type_entity_id_occurred_at_id"],
            StringComparison.Ordinal);
        Assert.Contains(
            "(organization_id, actor_user_id, occurred_at DESC, id DESC)",
            indexes["ix_audit_logs_org_actor_user_id_occurred_at_id"],
            StringComparison.Ordinal);
        Assert.Contains(
            "(organization_id, event_type, occurred_at DESC, id DESC)",
            indexes["ix_audit_logs_org_event_type_occurred_at_id"],
            StringComparison.Ordinal);
    }

    private async Task AssertAppendOnlyTriggerAsync()
    {
        const string Query =
            """
            SELECT pg_get_triggerdef(trigger.oid), trigger.tgenabled
            FROM pg_trigger AS trigger
            WHERE trigger.tgrelid = 'public.audit_logs'::regclass
              AND trigger.tgname = 'trg_audit_logs_append_only'
              AND NOT trigger.tgisinternal
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        string triggerDefinition = reader.GetString(0);
        Assert.Contains("BEFORE", triggerDefinition, StringComparison.Ordinal);
        Assert.Contains("UPDATE", triggerDefinition, StringComparison.Ordinal);
        Assert.Contains("DELETE", triggerDefinition, StringComparison.Ordinal);
        Assert.Contains("TRUNCATE", triggerDefinition, StringComparison.Ordinal);
        Assert.Contains(
            "FOR EACH STATEMENT",
            triggerDefinition,
            StringComparison.Ordinal);
        Assert.Equal('O', reader.GetChar(1));
        Assert.False(await reader.ReadAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task<string[]> GetPublicTablesAsync()
    {
        const string Query =
            """
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename <> '__EFMigrationsHistory'
            ORDER BY tablename
            """;
        var tables = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private async Task<int> CountDuplicateMembershipIdsAsync()
    {
        const string Query =
            """
            SELECT count(*)::integer
            FROM
            (
                SELECT id
                FROM organization_memberships
                GROUP BY id
                HAVING count(*) > 1
            ) AS duplicates
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    private async Task<string?> GetConstraintColumnsAsync(
        string tableName,
        string constraintName)
    {
        const string Query =
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private async Task<string[]> GetConstraintNamesAsync(string tableName)
    {
        const string Query =
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = ('public.' || @tableName)::regclass
              AND contype IN ('p', 'f', 'c')
            ORDER BY conname
            """;
        var names = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("tableName", tableName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private async Task<string?> GetForeignKeyShapeAsync(string constraintName)
    {
        const string Query =
            """
            SELECT
                string_agg(source.column_name, ',' ORDER BY source.ordinal_position)
                || '=>'
                || string_agg(target.column_name, ',' ORDER BY source.ordinal_position)
                || '|'
                || referential.delete_rule
            FROM information_schema.referential_constraints AS referential
            INNER JOIN information_schema.key_column_usage AS source
                ON source.constraint_schema = referential.constraint_schema
                AND source.constraint_name = referential.constraint_name
            INNER JOIN information_schema.key_column_usage AS target
                ON target.constraint_schema = referential.unique_constraint_schema
                AND target.constraint_name = referential.unique_constraint_name
                AND target.ordinal_position = source.position_in_unique_constraint
            WHERE referential.constraint_schema = 'public'
              AND referential.constraint_name = @constraintName
            GROUP BY referential.delete_rule
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private async Task<Dictionary<string, string>> GetIndexesAsync()
    {
        const string Query =
            """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'audit_logs'
            ORDER BY indexname
            """;
        var indexes = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0), reader.GetString(1));
        }

        return indexes;
    }
}
