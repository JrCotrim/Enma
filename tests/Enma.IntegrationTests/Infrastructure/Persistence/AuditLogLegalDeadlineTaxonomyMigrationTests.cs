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
public sealed class AuditLogLegalDeadlineTaxonomyMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261001141428_AddLegalDeadlineResponsible";
    private const string CurrentMigration =
        "20261001141525_ExtendAuditTaxonomyForLegalDeadlineResponsible";
    private const string ResponsibleChanged =
        "{\"oldResponsibleMembershipId\":null," +
        "\"newResponsibleMembershipId\":\"11111111-1111-1111-1111-111111111111\"}";
    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        10,
        1,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => fixture.RestoreLatestSchemaAsync();

    [Fact]
    public async Task MigrateAsync_DownAndUp_PreservesExistingTaxonomyRows()
    {
        await MigrateAsync(PreviousMigration);
        ActorGraph graph = await SeedActorGraphAsync();

        await InsertRawAuditLogAsync(graph, 11, 5, null);
        await InsertRawAuditLogAsync(graph, 12, 5, "{\"changedFields\":[1]}");
        await InsertRawAuditLogAsync(graph, 14, 5, null);
        await InsertRawAuditLogAsync(graph, 35, 4, ResponsibleChanged);
        PostgresException beforeUpgrade = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawAuditLogAsync(graph, 36, 5, ResponsibleChanged));
        Assert.Equal(PostgresErrorCodes.CheckViolation, beforeUpgrade.SqlState);
        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync(CurrentMigration);

        Assert.Equal(tablesBefore, await GetPublicTablesAsync());
        Assert.Equal(4, await CountAuditLogsAsync());

        await MigrateAsync(PreviousMigration);
        Assert.Equal(4, await CountAuditLogsAsync());

        PostgresException rollbackException =
            await Assert.ThrowsAsync<PostgresException>(
                () => InsertRawAuditLogAsync(graph, 36, 5, ResponsibleChanged));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            rollbackException.SqlState);

        await MigrateAsync(CurrentMigration);
        Assert.Equal(4, await CountAuditLogsAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CurrentSchema_AcceptsLegalDeadlineResponsibleTaxonomyAndRejectsInvalidShapes()
    {
        ActorGraph graph = await SeedActorGraphAsync();

        await InsertRawAuditLogAsync(graph, 36, 5, ResponsibleChanged);
        await InsertRawAuditLogAsync(graph, 11, 5, null);

        Assert.Equal(2, await CountAuditLogsAsync());

        foreach ((int eventType, int entityType, string? details) in new[]
        {
            (36, 4, (string?)ResponsibleChanged),
            (36, 1, (string?)ResponsibleChanged),
            (36, 6, (string?)ResponsibleChanged),
            (36, 5, (string?)null),
            (36, 5, (string?)"[]"),
            (11, 5, (string?)ResponsibleChanged),
            (37, 5, (string?)"{}")
        })
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRawAuditLogAsync(
                    graph,
                    eventType,
                    entityType,
                    details));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        Assert.Equal(2, await CountAuditLogsAsync());
    }

    private async Task<ActorGraph> SeedActorGraphAsync()
    {
        var organization = new Organization(
            "Deadline Audit Taxonomy Tenant",
            "deadline-audit-taxonomy-tenant",
            OccurredAt.AddDays(-1));
        var user = new User(
            "Deadline Audit Taxonomy Actor",
            "deadline.audit.taxonomy.actor@example.test",
            OccurredAt.AddDays(-1));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            OccurredAt.AddDays(-1));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, user, membership);
        await dbContext.SaveChangesAsync();

        return new ActorGraph(organization.Id, user.Id, membership.Id);
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
        command.Parameters.AddWithValue("actorRole", (int)OrganizationRole.Owner);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("entityId", Guid.NewGuid());
        command.Parameters.AddWithValue("occurredAt", OccurredAt);
        command.Parameters.Add(
            new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
            {
                Value = details is null ? DBNull.Value : details
            });

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAuditLogsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM audit_logs",
            connection);

        return Assert.IsType<int>(await command.ExecuteScalarAsync());
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

    private sealed record ActorGraph(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId);
}
