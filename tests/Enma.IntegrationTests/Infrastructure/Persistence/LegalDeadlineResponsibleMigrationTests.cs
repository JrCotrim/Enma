using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineResponsibleMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260929115943_ExtendAuditTaxonomyForLegalProcessOperations";

    private static readonly DateTimeOffset CreatedAt = new(
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
    public async Task MigrateAsync_FromPreviousSchema_PreservesLegacyDeadlinesWithoutResponsible()
    {
        await MigrateAsync(PreviousMigration);
        TenantGraph graph = CreateTenantGraph("legacy");
        var pendingDeadline = new LegalDeadline(
            graph.Organization.Id,
            graph.LegalProcess.Id,
            "Legacy Pending Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);
        var completedDeadline = new LegalDeadline(
            graph.Organization.Id,
            graph.LegalProcess.Id,
            "Legacy Completed Deadline",
            new DateOnly(2026, 11, 2),
            CreatedAt);
        completedDeadline.Complete(CreatedAt.AddHours(1));

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(graph.Organization, graph.User, graph.Membership);
            await seedContext.SaveChangesAsync();
            await PostgreSqlFixture.InsertClientWithoutPersonTypeColumnsAsync(
                seedContext,
                graph.Client);
            seedContext.Add(graph.LegalProcess);
            await seedContext.SaveChangesAsync();
            await PostgreSqlFixture.InsertLegalDeadlineWithoutResponsibleColumnAsync(
                seedContext,
                pendingDeadline);
            await PostgreSqlFixture.InsertLegalDeadlineWithoutResponsibleColumnAsync(
                seedContext,
                completedDeadline);
        }

        Assert.False(await ResponsibleColumnExistsAsync());

        await MigrateAsync();

        Assert.True(await ResponsibleColumnExistsAsync());
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        LegalDeadline[] persisted = await assertionContext.LegalDeadlines
            .OrderBy(legalDeadline => legalDeadline.DueDate)
            .ToArrayAsync();
        Assert.Equal(2, persisted.Length);
        Assert.Equal(pendingDeadline.Id, persisted[0].Id);
        Assert.Equal("Legacy Pending Deadline", persisted[0].Title);
        Assert.Null(persisted[0].CompletedAt);
        Assert.Equal(completedDeadline.Id, persisted[1].Id);
        Assert.Equal(CreatedAt.AddHours(1), persisted[1].CompletedAt);
        Assert.All(persisted, legalDeadline =>
            Assert.Null(legalDeadline.ResponsibleMembershipId));
        Assert.False(assertionContext.Database.HasPendingModelChanges());

        persisted[0].ChangeResponsible(graph.Membership.Id);
        await assertionContext.SaveChangesAsync();
        Assert.Equal(
            graph.Membership.Id,
            await QueryResponsibleAsync(pendingDeadline.Id));
    }

    [Fact]
    public async Task CurrentSchema_ForeignKeyRejectsCrossTenantOrMissingResponsible()
    {
        TenantGraph tenantA = CreateTenantGraph("tenant-a");
        TenantGraph tenantB = CreateTenantGraph("tenant-b");
        var legalDeadline = new LegalDeadline(
            tenantA.Organization.Id,
            tenantA.LegalProcess.Id,
            "Tenant A Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(GetGraphEntities(tenantA));
            seedContext.AddRange(GetGraphEntities(tenantB));
            seedContext.Add(legalDeadline);
            await seedContext.SaveChangesAsync();
        }

        PostgresException crossTenant = await Assert.ThrowsAsync<PostgresException>(
            () => UpdateResponsibleAsync(legalDeadline.Id, tenantB.Membership.Id));
        PostgresException missing = await Assert.ThrowsAsync<PostgresException>(
            () => UpdateResponsibleAsync(
                legalDeadline.Id,
                Guid.Parse("3d6f9b2e-8a1c-4e7f-b5d3-0c2a9e6f1b48")));
        PostgresException crossTenantInsert =
            await Assert.ThrowsAsync<PostgresException>(
                () => InsertDeadlineWithResponsibleAsync(
                    tenantA,
                    tenantB.Membership.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missing.SqlState);
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            crossTenantInsert.SqlState);
        Assert.Null(await QueryResponsibleAsync(legalDeadline.Id));

        await UpdateResponsibleAsync(legalDeadline.Id, tenantA.Membership.Id);
        Assert.Equal(
            tenantA.Membership.Id,
            await QueryResponsibleAsync(legalDeadline.Id));

        PostgresException restrictedDelete =
            await Assert.ThrowsAsync<PostgresException>(
                () => DeleteMembershipAsync(tenantA.Membership.Id));
        Assert.Equal(
            PostgresErrorCodes.RestrictViolation,
            restrictedDelete.SqlState);
        Assert.Equal(
            tenantA.Membership.Id,
            await QueryResponsibleAsync(legalDeadline.Id));
    }

    [Fact]
    public async Task MigrateAsync_DownAndUp_PreservesDeadlinesAndRestoresColumn()
    {
        TenantGraph graph = CreateTenantGraph("round-trip");
        var legalDeadline = new LegalDeadline(
            graph.Organization.Id,
            graph.LegalProcess.Id,
            "Round Trip Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(GetGraphEntities(graph));
            seedContext.Add(legalDeadline);
            await seedContext.SaveChangesAsync();
        }

        await MigrateAsync(PreviousMigration);

        Assert.False(await ResponsibleColumnExistsAsync());
        Assert.Equal(1, await CountDeadlinesAsync());

        await MigrateAsync();

        Assert.True(await ResponsibleColumnExistsAsync());
        Assert.Equal(1, await CountDeadlinesAsync());
        Assert.Null(await QueryResponsibleAsync(legalDeadline.Id));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private static TenantGraph CreateTenantGraph(string slug)
    {
        var organization = new Organization(
            $"Deadline Responsible {slug}",
            $"deadline-responsible-{slug}",
            CreatedAt);
        var user = new User(
            $"Deadline Responsible {slug}",
            $"deadline.responsible.{slug}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"Deadline Responsible Client {slug}",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"Deadline Responsible Process {slug}",
            CreatedAt);

        return new TenantGraph(organization, user, membership, client, legalProcess);
    }

    private static object[] GetGraphEntities(TenantGraph graph)
    {
        return
        [
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            graph.LegalProcess
        ];
    }

    private async Task UpdateResponsibleAsync(Guid deadlineId, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE legal_deadlines
            SET responsible_membership_id = @membershipId
            WHERE id = @deadlineId
            """,
            connection);
        command.Parameters.AddWithValue("membershipId", membershipId);
        command.Parameters.AddWithValue("deadlineId", deadlineId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertDeadlineWithResponsibleAsync(
        TenantGraph graph,
        Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO legal_deadlines
                (id, organization_id, process_id, title, due_date, created_at,
                 responsible_membership_id)
            VALUES
                (@id, @organizationId, @processId, 'Cross Tenant Responsible',
                 DATE '2026-11-03', @createdAt, @membershipId)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organizationId", graph.Organization.Id);
        command.Parameters.AddWithValue("processId", graph.LegalProcess.Id);
        command.Parameters.AddWithValue("createdAt", CreatedAt);
        command.Parameters.AddWithValue("membershipId", membershipId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteMembershipAsync(Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM organization_memberships WHERE id = @membershipId",
            connection);
        command.Parameters.AddWithValue("membershipId", membershipId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid?> QueryResponsibleAsync(Guid deadlineId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT responsible_membership_id
            FROM legal_deadlines
            WHERE id = @deadlineId
            """,
            connection);
        command.Parameters.AddWithValue("deadlineId", deadlineId);
        object? value = await command.ExecuteScalarAsync();

        return value is DBNull or null ? null : (Guid)value;
    }

    private async Task<int> CountDeadlinesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM legal_deadlines",
            connection);

        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    private async Task<bool> ResponsibleColumnExistsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'legal_deadlines'
                  AND column_name = 'responsible_membership_id'
            )
            """,
            connection);

        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private sealed record TenantGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client Client,
        LegalProcess LegalProcess);
}
