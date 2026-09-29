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
public sealed class AuditLogLegalProcessTaxonomyMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260928172344_AddLegalProcessOperationalFields";
    private const string CurrentMigration =
        "20260929115943_ExtendAuditTaxonomyForLegalProcessOperations";
    private const string DetailsChanged = "{\"changedFields\":[1,2]}";
    private const string StatusChanged = "{\"oldStatus\":3,\"newStatus\":1}";
    private const string ResponsibleChanged =
        "{\"oldResponsibleMembershipId\":null," +
        "\"newResponsibleMembershipId\":\"11111111-1111-1111-1111-111111111111\"}";
    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        9,
        29,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => MigrateAsync();

    [Fact]
    public async Task MigrateAsync_DownAndUp_PreservesExistingTaxonomyRows()
    {
        await MigrateAsync(PreviousMigration);
        ActorGraph graph = await SeedActorGraphAsync();

        await InsertRawAuditLogAsync(graph, 9, 4, null);
        await InsertRawAuditLogAsync(graph, 10, 4, null);
        await InsertRawAuditLogAsync(graph, 12, 5, "{\"changedFields\":[1]}");
        await InsertRawAuditLogAsync(graph, 32, 8, null);
        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync(CurrentMigration);

        Assert.Equal(tablesBefore, await GetPublicTablesAsync());
        Assert.Equal(4, await CountAuditLogsAsync());

        await MigrateAsync(PreviousMigration);
        Assert.Equal(4, await CountAuditLogsAsync());

        PostgresException rollbackException =
            await Assert.ThrowsAsync<PostgresException>(
                () => InsertRawAuditLogAsync(graph, 33, 4, DetailsChanged));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            rollbackException.SqlState);

        await MigrateAsync(CurrentMigration);
        Assert.Equal(4, await CountAuditLogsAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CurrentSchema_AcceptsLegalProcessTaxonomyAndRejectsInvalidShapes()
    {
        ActorGraph graph = await SeedActorGraphAsync();

        await InsertRawAuditLogAsync(graph, 33, 4, DetailsChanged);
        await InsertRawAuditLogAsync(graph, 34, 4, StatusChanged);
        await InsertRawAuditLogAsync(graph, 35, 4, ResponsibleChanged);

        Assert.Equal(3, await CountAuditLogsAsync());

        foreach ((int eventType, int entityType, string? details) in new[]
        {
            (33, 5, (string?)DetailsChanged),
            (34, 1, (string?)StatusChanged),
            (35, 2, (string?)ResponsibleChanged),
            (33, 4, (string?)null),
            (34, 4, (string?)null),
            (35, 4, (string?)null),
            (34, 4, (string?)"[]"),
            (36, 4, (string?)"{}")
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

        Assert.Equal(3, await CountAuditLogsAsync());
    }

    private async Task<ActorGraph> SeedActorGraphAsync()
    {
        var organization = new Organization(
            "Process Audit Taxonomy Tenant",
            "process-audit-taxonomy-tenant",
            OccurredAt.AddDays(-1));
        var user = new User(
            "Process Audit Taxonomy Actor",
            "process.audit.taxonomy.actor@example.test",
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
