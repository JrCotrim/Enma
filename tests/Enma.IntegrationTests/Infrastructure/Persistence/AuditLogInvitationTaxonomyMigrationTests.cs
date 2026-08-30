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
public sealed class AuditLogInvitationTaxonomyMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260830175257_AddOrganizationInvitations";
    private const string CurrentMigration =
        "20260830224431_ExtendAuditTaxonomyForOrganizationInvitations";
    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        8,
        30,
        18,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrateAsync_DownAndUp_PreservesAllLegacyTaxonomyRows()
    {
        await MigrateAsync(PreviousMigration);
        ActorGraph graph = await SeedActorGraphAsync();

        foreach ((int eventType, int entityType, string? details) in
            LegacyTaxonomyCases())
        {
            await InsertRawAuditLogAsync(
                graph,
                eventType,
                entityType,
                details);
        }

        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync(CurrentMigration);

        Assert.Equal(tablesBefore, await GetPublicTablesAsync());
        Assert.Equal(24, await CountAuditLogsAsync());

        await MigrateAsync(PreviousMigration);
        Assert.Equal(24, await CountAuditLogsAsync());

        await MigrateAsync(CurrentMigration);
        Assert.Equal(24, await CountAuditLogsAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CurrentSchema_AcceptsInvitationTaxonomyAndRejectsInvalidShapes()
    {
        ActorGraph graph = await SeedActorGraphAsync();

        await InsertRawAuditLogAsync(graph, 25, 9, "{\"role\":3}");
        await InsertRawAuditLogAsync(graph, 26, 9, null);
        await InsertRawAuditLogAsync(graph, 27, 9, null);
        await InsertRawAuditLogAsync(graph, 28, 9, null);
        await InsertRawAuditLogAsync(graph, 24, 8, null);

        Assert.Equal(5, await CountAuditLogsAsync());

        foreach ((int eventType, int entityType, string? details) in new[]
        {
            (999, 9, (string?)null),
            (26, 999, (string?)null),
            (25, 8, (string?)"{\"role\":3}"),
            (25, 9, (string?)null),
            (26, 9, (string?)"{}")
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

        Assert.Equal(5, await CountAuditLogsAsync());
    }

    private async Task<ActorGraph> SeedActorGraphAsync()
    {
        var organization = new Organization(
            "Invitation Audit Taxonomy Tenant",
            "invitation-audit-taxonomy-tenant",
            OccurredAt.AddDays(-1));
        var user = new User(
            "Invitation Audit Taxonomy Actor",
            "invitation.audit.taxonomy.actor@example.test",
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

    private async Task MigrateAsync(string targetMigration)
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

    private static IEnumerable<(int EventType, int EntityType, string? Details)>
        LegacyTaxonomyCases()
    {
        for (int eventType = 1; eventType <= 24; eventType++)
        {
            int entityType = eventType switch
            {
                1 => 1,
                <= 4 => 2,
                <= 8 => 3,
                <= 10 => 4,
                <= 14 => 5,
                <= 19 => 6,
                <= 23 => 7,
                _ => 8
            };
            string? details = eventType switch
            {
                1 => "{\"oldName\":\"Old\",\"newName\":\"New\"}",
                2 => "{\"oldRole\":3,\"newRole\":2}",
                12 or 16 or 21 => "{\"changedFields\":[1]}",
                17 or 22 =>
                    "{\"oldAssigneeMembershipId\":null," +
                    "\"newAssigneeMembershipId\":" +
                    "\"11111111-1111-1111-1111-111111111111\"}",
                _ => null
            };

            yield return (eventType, entityType, details);
        }
    }

    private sealed record ActorGraph(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId);
}
