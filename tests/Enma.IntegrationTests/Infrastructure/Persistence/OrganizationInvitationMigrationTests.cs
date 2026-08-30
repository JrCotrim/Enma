using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationInvitationMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260827182006_AddAuditLogs";
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        30,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrateAsync_FromPreviousSchema_PreservesDataAndCreatesInvitationSchema()
    {
        await MigrateAsync(PreviousMigration);
        var organization = new Organization(
            "Existing Invitation Tenant",
            "existing-invitation-tenant",
            CreatedAt);
        var user = new User(
            "Existing Invitation Creator",
            "existing.invitation.creator@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Administrator,
            CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(organization, user, membership);
            await seedContext.SaveChangesAsync();
        }

        string[] tablesBefore = await GetPublicTablesAsync();
        Assert.DoesNotContain("organization_invitations", tablesBefore);

        await MigrateAsync();

        Assert.Equal(
            tablesBefore
                .Append("organization_invitations")
                .Order(StringComparer.Ordinal),
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
              AND table_name = 'organization_invitations'
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
                "invited_email|character varying|NO|254",
                "role|integer|NO|",
                "created_by_membership_id|uuid|NO|",
                "token_hash|bytea|YES|",
                "created_at|timestamp with time zone|NO|",
                "token_issued_at|timestamp with time zone|NO|",
                "expires_at|timestamp with time zone|NO|",
                "accepted_at|timestamp with time zone|YES|",
                "accepted_by_user_id|uuid|YES|",
                "revoked_at|timestamp with time zone|YES|",
                "expired_at|timestamp with time zone|YES|"
            ],
            actual);
    }

    private async Task AssertConstraintsAsync()
    {
        string[] expectedConstraints =
        [
            "ck_organization_invitations_acceptance_time",
            "ck_organization_invitations_accepted_by_user",
            "ck_organization_invitations_expiration",
            "ck_organization_invitations_expired_at",
            "ck_organization_invitations_revocation_time",
            "ck_organization_invitations_role",
            "ck_organization_invitations_terminal_state",
            "ck_organization_invitations_token_hash_length",
            "ck_organization_invitations_token_issued_at",
            "ck_organization_invitations_token_state",
            "fk_organization_invitations_memberships_org_created_by_id",
            "fk_organization_invitations_organizations_organization_id",
            "fk_organization_invitations_users_accepted_by_user_id",
            "pk_organization_invitations"
        ];

        Assert.Equal(
            expectedConstraints,
            await GetConstraintNamesAsync());
        Assert.Equal(
            "organization_id,created_by_membership_id=>" +
            "organization_id,id|RESTRICT",
            await GetForeignKeyShapeAsync(
                "fk_organization_invitations_memberships_org_created_by_id"));
        Assert.Equal(
            "organization_id=>id|RESTRICT",
            await GetForeignKeyShapeAsync(
                "fk_organization_invitations_organizations_organization_id"));
        Assert.Equal(
            "accepted_by_user_id=>id|RESTRICT",
            await GetForeignKeyShapeAsync(
                "fk_organization_invitations_users_accepted_by_user_id"));
    }

    private async Task AssertIndexesAsync()
    {
        Dictionary<string, string> indexes = await GetIndexesAsync();
        string[] expectedNames =
        [
            "ix_organization_invitations_accepted_by_user_id",
            "ix_organization_invitations_org_created_by_membership_id",
            "ix_organization_invitations_organization_id_created_at_id",
            "pk_organization_invitations",
            "ux_organization_invitations_open_organization_id_email",
            "ux_organization_invitations_token_hash"
        ];

        Assert.Equal(expectedNames, indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(
            "(organization_id, created_at DESC, id DESC)",
            indexes["ix_organization_invitations_organization_id_created_at_id"],
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes["ux_organization_invitations_token_hash"],
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE (token_hash IS NOT NULL)",
            indexes["ux_organization_invitations_token_hash"],
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes[
                "ux_organization_invitations_open_organization_id_email"],
            StringComparison.Ordinal);
        Assert.Contains(
            "(organization_id, invited_email)",
            indexes[
                "ux_organization_invitations_open_organization_id_email"],
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE ((accepted_at IS NULL) AND (revoked_at IS NULL) " +
            "AND (expired_at IS NULL))",
            indexes[
                "ux_organization_invitations_open_organization_id_email"],
            StringComparison.Ordinal);
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

    private async Task<string[]> GetConstraintNamesAsync()
    {
        const string Query =
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'public.organization_invitations'::regclass
              AND contype IN ('p', 'f', 'c')
            ORDER BY conname
            """;
        var names = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
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
              AND tablename = 'organization_invitations'
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
