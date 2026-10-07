using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationOwnerInvariantMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261003184309_ExtendAuditTaxonomyForPaymentReversal";
    private const string OwnerMigration =
        "20261006144133_AddSingleOrganizationOwnerIndex";
    private const string TaxonomyMigration =
        "20261006144227_ExtendAuditTaxonomyForOwnershipTransfer";
    private const string SingleOwnerIndex =
        "ux_organization_memberships_single_owner";
    private const string OwnerActiveCheck =
        "ck_organization_memberships_owner_active";
    private const int Owner = 1;
    private const int Administrator = 2;
    private const int Member = 3;

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    // Memberships are removed first so a failed precheck scenario cannot keep
    // the shared database below the latest migration.
    public async Task DisposeAsync()
    {
        try
        {
            await fixture.ResetDatabaseAsync();
        }
        finally
        {
            await fixture.RestoreLatestSchemaAsync();
        }
    }

    [Fact]
    public async Task MigrateAsync_WithOwnerInvariantViolations_FailsReportingOnlyCounts()
    {
        await MigrateAsync(PreviousMigration);
        Organization[] organizations =
        [
            CreateOrganization("mixed-owners"),
            CreateOrganization("three-owners"),
            CreateOrganization("inactive-owner"),
            CreateOrganization("inactive-admin"),
            CreateOrganization("valid")
        ];
        User[] users = Enumerable.Range(1, 11)
            .Select(index => CreateUser($"Owner Invariant {index}"))
            .ToArray();
        await SeedAsync([.. organizations, .. users]);
        var violatingMemberships = new List<Guid>
        {
            await InsertRawMembershipAsync(organizations[0], users[0], Owner, true),
            await InsertRawMembershipAsync(organizations[0], users[1], Owner, false),
            await InsertRawMembershipAsync(organizations[1], users[2], Owner, true),
            await InsertRawMembershipAsync(organizations[1], users[3], Owner, true),
            await InsertRawMembershipAsync(organizations[1], users[4], Owner, true),
            await InsertRawMembershipAsync(organizations[2], users[5], Owner, false),
            await InsertRawMembershipAsync(organizations[3], users[6], Owner, false)
        };
        Guid[] validMemberships =
        [
            await InsertRawMembershipAsync(organizations[3], users[7], Administrator, false),
            await InsertRawMembershipAsync(organizations[4], users[8], Owner, true),
            await InsertRawMembershipAsync(organizations[4], users[9], Administrator, false),
            await InsertRawMembershipAsync(organizations[4], users[10], Member, false)
        ];

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => MigrateAsync());

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        Assert.Equal(
            "organization_memberships violates the single active Owner invariant: "
                + "2 organization(s) with more than one Owner membership; "
                + "3 inactive Owner membership(s)",
            exception.MessageText);
        string diagnostics = string.Join(
            "\n",
            exception.ToString(),
            exception.MessageText,
            exception.Detail,
            exception.Hint,
            exception.Where,
            exception.InternalQuery);

        foreach (Guid identifier in organizations.Select(organization => organization.Id)
                     .Concat(users.Select(user => user.Id))
                     .Concat(violatingMemberships)
                     .Concat(validMemberships))
        {
            Assert.DoesNotContain(identifier.ToString("D"), diagnostics);
            Assert.DoesNotContain(identifier.ToString("N"), diagnostics);
        }

        Assert.DoesNotContain("@example.test", diagnostics);
        Assert.False(await MigrationAppliedAsync(OwnerMigration));
        Assert.False(await IndexExistsAsync(SingleOwnerIndex));
        Assert.False(await ConstraintExistsAsync(OwnerActiveCheck));
        Assert.Equal(11, await CountMembershipsAsync());

        // Keep the first active Owner of each organization and drop the rest.
        await DeleteMembershipsAsync(
            violatingMemberships[1],
            violatingMemberships[3],
            violatingMemberships[4],
            violatingMemberships[5],
            violatingMemberships[6]);

        await MigrateAsync();

        Assert.True(await MigrationAppliedAsync(OwnerMigration));
        Assert.True(await MigrationAppliedAsync(TaxonomyMigration));
        Assert.True(await IndexExistsAsync(SingleOwnerIndex));
        Assert.True(await ConstraintExistsAsync(OwnerActiveCheck));
        Assert.Equal(6, await CountMembershipsAsync());
    }

    [Fact]
    public async Task CurrentSchema_RejectsSecondOwnerAndInactiveOwner()
    {
        Organization organization = CreateOrganization("current");
        Organization otherOrganization = CreateOrganization("other");
        User[] users = Enumerable.Range(1, 6)
            .Select(index => CreateUser($"Current Schema {index}"))
            .ToArray();
        await SeedAsync([organization, otherOrganization, .. users]);
        Guid owner = await InsertRawMembershipAsync(organization, users[0], Owner, true);
        Guid administrator = await InsertRawMembershipAsync(
            organization,
            users[1],
            Administrator,
            true);

        PostgresException secondOwner = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawMembershipAsync(organization, users[2], Owner, true));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, secondOwner.SqlState);
        Assert.Equal(SingleOwnerIndex, secondOwner.ConstraintName);

        PostgresException promotion = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                "UPDATE organization_memberships SET role = 1 WHERE id = @id",
                ("id", administrator)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, promotion.SqlState);
        Assert.Equal(SingleOwnerIndex, promotion.ConstraintName);

        PostgresException inactiveInsert = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawMembershipAsync(otherOrganization, users[3], Owner, false));
        Assert.Equal(PostgresErrorCodes.CheckViolation, inactiveInsert.SqlState);
        Assert.Equal(OwnerActiveCheck, inactiveInsert.ConstraintName);

        PostgresException deactivation = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                "UPDATE organization_memberships SET is_active = false WHERE id = @id",
                ("id", owner)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, deactivation.SqlState);
        Assert.Equal(OwnerActiveCheck, deactivation.ConstraintName);

        // Allowed shapes: another tenant's Owner, inactive non-Owners, and an
        // ordered demote-then-promote swap inside one transaction.
        await InsertRawMembershipAsync(otherOrganization, users[3], Owner, true);
        await InsertRawMembershipAsync(organization, users[4], Administrator, false);
        await InsertRawMembershipAsync(organization, users[5], Member, false);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync();
            await ExecuteAsync(
                connection,
                "UPDATE organization_memberships SET role = 2 WHERE id = @id",
                ("id", owner));
            await ExecuteAsync(
                connection,
                "UPDATE organization_memberships SET role = 1 WHERE id = @id",
                ("id", administrator));
            await transaction.CommitAsync();
        }

        Assert.Equal(
            [administrator],
            await FindOwnerMembershipIdsAsync(organization.Id));
    }

    [Fact]
    public async Task MigrateAsync_DownAndUp_RemovesAndRestoresOwnerConstraints()
    {
        Organization organization = CreateOrganization("round-trip");
        User owner = CreateUser("Round Trip Owner");
        User administrator = CreateUser("Round Trip Administrator");
        await SeedAsync(organization, owner, administrator);
        await InsertRawMembershipAsync(organization, owner, Owner, true);
        await InsertRawMembershipAsync(organization, administrator, Administrator, false);
        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync(PreviousMigration);

        Assert.False(await IndexExistsAsync(SingleOwnerIndex));
        Assert.False(await ConstraintExistsAsync(OwnerActiveCheck));
        Assert.Equal(tablesBefore, await GetPublicTablesAsync());
        Assert.Equal(2, await CountMembershipsAsync());

        await MigrateAsync(OwnerMigration);

        Assert.True(await IndexExistsAsync(SingleOwnerIndex));
        Assert.True(await ConstraintExistsAsync(OwnerActiveCheck));
        Assert.Equal(
            "CREATE UNIQUE INDEX ux_organization_memberships_single_owner " +
                "ON public.organization_memberships USING btree (organization_id) " +
                "WHERE (role = 1)",
            await GetIndexDefinitionAsync(SingleOwnerIndex));
        Assert.Equal(2, await CountMembershipsAsync());

        await MigrateAsync();

        Assert.Equal(2, await CountMembershipsAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigrateAsync_TaxonomyDownAndUp_GatesOwnershipTransferredEvent()
    {
        await MigrateAsync(OwnerMigration);
        ActorGraph graph = await SeedActorGraphAsync();
        string details = CreateTransferDetails(graph.MembershipId, Guid.NewGuid());

        await InsertRawAuditLogAsync(graph, 1, 1, "{\"oldName\":\"A\",\"newName\":\"B\"}");
        PostgresException beforeUpgrade = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawAuditLogAsync(graph, 38, 1, details));
        Assert.Equal(PostgresErrorCodes.CheckViolation, beforeUpgrade.SqlState);

        await MigrateAsync(TaxonomyMigration);
        Assert.Equal(1, await CountAuditLogsAsync());

        await MigrateAsync(OwnerMigration);
        PostgresException rollback = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawAuditLogAsync(graph, 38, 1, details));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rollback.SqlState);
        Assert.Equal(1, await CountAuditLogsAsync());

        await MigrateAsync(TaxonomyMigration);
        await InsertRawAuditLogAsync(graph, 38, 1, details);
        Assert.Equal(2, await CountAuditLogsAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CurrentSchema_AcceptsOwnershipTransferredOnlyWithOrganizationAndDetails()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        string details = CreateTransferDetails(graph.MembershipId, Guid.NewGuid());

        await InsertRawAuditLogAsync(graph, 38, 1, details);
        Assert.Equal(1, await CountAuditLogsAsync());

        foreach ((int eventType, int entityType, string? candidateDetails) in new[]
        {
            (38, 2, (string?)details),
            (38, 9, (string?)details),
            (38, 1, (string?)null),
            (38, 1, (string?)"[]"),
            (38, 1, (string?)"\"transferred\""),
            (39, 1, (string?)details)
        })
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRawAuditLogAsync(
                    graph,
                    eventType,
                    entityType,
                    candidateDetails));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        Assert.Equal(1, await CountAuditLogsAsync());
    }

    private async Task<ActorGraph> SeedActorGraphAsync()
    {
        Organization organization = CreateOrganization("taxonomy");
        User user = CreateUser("Ownership Taxonomy Actor");
        await SeedAsync(organization, user);
        Guid membershipId = await InsertRawMembershipAsync(
            organization,
            user,
            Owner,
            true);

        return new ActorGraph(organization.Id, user.Id, membershipId);
    }

    private static string CreateTransferDetails(
        Guid previousOwnerMembershipId,
        Guid newOwnerMembershipId)
    {
        return
            $"{{\"previousOwnerMembershipId\":\"{previousOwnerMembershipId:D}\"," +
            $"\"newOwnerMembershipId\":\"{newOwnerMembershipId:D}\"}}";
    }

    private async Task<Guid> InsertRawMembershipAsync(
        Organization organization,
        User user,
        int role,
        bool isActive)
    {
        var membershipId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO organization_memberships
                (id, organization_id, user_id, role, is_active, created_at)
            VALUES
                (@id, @organizationId, @userId, @role, @isActive, @createdAt)
            """,
            ("id", membershipId),
            ("organizationId", organization.Id),
            ("userId", user.Id),
            ("role", role),
            ("isActive", isActive),
            ("createdAt", CreatedAt));
        return membershipId;
    }

    private async Task InsertRawAuditLogAsync(
        ActorGraph graph,
        int eventType,
        int entityType,
        string? details)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_logs
            (
                id,
                organization_id,
                actor_user_id,
                actor_membership_id,
                actor_role_at_occurrence,
                event_type,
                entity_type,
                entity_id,
                occurred_at,
                details,
                trace_id
            )
            VALUES
            (
                @id,
                @organizationId,
                @actorUserId,
                @actorMembershipId,
                @actorRole,
                @eventType,
                @entityType,
                @entityId,
                @occurredAt,
                @details,
                NULL
            )
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organizationId", graph.OrganizationId);
        command.Parameters.AddWithValue("actorUserId", graph.UserId);
        command.Parameters.AddWithValue("actorMembershipId", graph.MembershipId);
        command.Parameters.AddWithValue("actorRole", Owner);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("entityId", graph.OrganizationId);
        command.Parameters.AddWithValue("occurredAt", CreatedAt.AddHours(1));
        command.Parameters.Add(
            new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
            {
                Value = details is null ? DBNull.Value : details
            });

        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql, parameters);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteMembershipsAsync(params Guid[] membershipIds)
    {
        await ExecuteAsync(
            "DELETE FROM organization_memberships WHERE id = ANY (@ids)",
            ("ids", membershipIds));
    }

    private async Task<int> CountMembershipsAsync()
    {
        return await ScalarAsync<int>(
            "SELECT count(*)::integer FROM organization_memberships");
    }

    private async Task<int> CountAuditLogsAsync()
    {
        return await ScalarAsync<int>("SELECT count(*)::integer FROM audit_logs");
    }

    private async Task<Guid[]> FindOwnerMembershipIdsAsync(Guid organizationId)
    {
        var identifiers = new List<Guid>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id FROM organization_memberships
            WHERE organization_id = @organizationId AND role = 1
            """,
            connection);
        command.Parameters.AddWithValue("organizationId", organizationId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            identifiers.Add(reader.GetGuid(0));
        }

        return identifiers.ToArray();
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        return await ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'organization_memberships'
                  AND indexname = @name)
            """,
            ("name", indexName));
    }

    private async Task<string?> GetIndexDefinitionAsync(string indexName)
    {
        return await ScalarAsync<string?>(
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND indexname = @name
            """,
            ("name", indexName));
    }

    private async Task<bool> ConstraintExistsAsync(string constraintName)
    {
        return await ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'public.organization_memberships'::regclass
                  AND conname = @name
                  AND contype = 'c')
            """,
            ("name", constraintName));
    }

    private async Task<bool> MigrationAppliedAsync(string migrationId)
    {
        return await ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = @migrationId)
            """,
            ("migrationId", migrationId));
    }

    private async Task<T> ScalarAsync<T>(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    private async Task<string[]> GetPublicTablesAsync()
    {
        var tables = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename <> '__EFMigrationsHistory'
            ORDER BY tablename
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"Owner Invariant {marker}",
            $"owner-invariant-{marker}-{Guid.NewGuid():N}",
            CreatedAt);
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            CreatedAt);
    }

    private sealed record ActorGraph(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId);
}
