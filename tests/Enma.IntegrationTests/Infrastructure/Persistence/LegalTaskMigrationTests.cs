using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260814173837_AddOrganizationMembershipRelationalIdentity";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        16,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MigrateAsync_FromEmptyDatabase_CreatesUsableLatestSchema()
    {
        await MigrateAsync(Migration.InitialDatabase);
        await MigrateAsync();
        RepresentativeGraph graph = CreateRepresentativeGraph();
        LegalTask legalTask = CreateLegalTask(graph);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(GetGraphEntities(graph).Append(legalTask));
            await dbContext.SaveChangesAsync();

            Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
        }

        Assert.Equal(
            "organization_id,process_id",
            await GetConstraintColumnsAsync(
                "fk_legal_tasks_legal_processes_organization_id_process_id"));
        Assert.Equal(
            "organization_id,created_by_membership_id",
            await GetConstraintColumnsAsync(
                "fk_legal_tasks_memberships_org_created_by_membership_id"));
        Assert.Equal(
            "organization_id,assignee_membership_id",
            await GetConstraintColumnsAsync(
                "fk_legal_tasks_memberships_org_assignee_membership_id"));
    }

    [Fact]
    public async Task MigrateAsync_FromPreTaskSchema_PreservesRepresentativeData()
    {
        await MigrateAsync(PreviousMigration);
        RepresentativeGraph graph = CreateRepresentativeGraph();
        DateTimeOffset deadlineCompletedAt = CreatedAt.AddDays(1);
        graph.LegalDeadline.Complete(deadlineCompletedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(GetGraphEntities(graph));
            await seedContext.SaveChangesAsync();
        }

        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync();

        string[] tablesAfter = await GetPublicTablesAsync();
        Assert.Equal(
            tablesBefore
                .Append("legal_tasks")
                .Append("legal_documents")
                .Append("calendar_events")
                .Append("notifications")
                .Append("audit_logs")
                .Append("organization_invitations")
                .OrderBy(table => table),
            tablesAfter);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations.SingleAsync();
        User[] users = await dbContext.Users
            .OrderBy(user => user.Email)
            .ToArrayAsync();
        OrganizationMembership[] memberships =
            await dbContext.OrganizationMemberships
                .OrderBy(membership => membership.Id)
                .ToArrayAsync();
        Client client = await dbContext.Clients.SingleAsync();
        LegalProcess legalProcess = await dbContext.LegalProcesses.SingleAsync();
        LegalDeadline legalDeadline = await dbContext.LegalDeadlines.SingleAsync();

        Assert.Equal(graph.Organization.Id, organization.Id);
        Assert.Equal("Enma Legal", organization.Name);
        Assert.Equal("enma-legal", organization.Slug);
        Assert.True(organization.IsActive);
        Assert.Equal(CreatedAt, organization.CreatedAt);
        Assert.Equal(2, users.Length);
        Assert.Equal(
            [graph.AssigneeUser.Id, graph.CreatorUser.Id],
            users.Select(user => user.Id).ToArray());
        Assert.All(users, user => Assert.True(user.IsActive));
        Assert.Equal(2, memberships.Length);
        Assert.Contains(
            memberships,
            membership => membership.Id == graph.CreatorMembership.Id &&
                membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == graph.CreatorUser.Id &&
                membership.Role == OrganizationRole.Administrator &&
                membership.IsActive &&
                membership.CreatedAt == CreatedAt);
        Assert.Contains(
            memberships,
            membership => membership.Id == graph.AssigneeMembership.Id &&
                membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == graph.AssigneeUser.Id &&
                membership.Role == OrganizationRole.Member &&
                membership.IsActive &&
                membership.CreatedAt == CreatedAt);
        Assert.Equal(graph.Client.Id, client.Id);
        Assert.Equal(graph.Organization.Id, client.OrganizationId);
        Assert.Equal("Existing Client", client.Name);
        Assert.True(client.IsActive);
        Assert.Equal(CreatedAt, client.CreatedAt);
        Assert.Equal(graph.LegalProcess.Id, legalProcess.Id);
        Assert.Equal(graph.Organization.Id, legalProcess.OrganizationId);
        Assert.Equal(graph.Client.Id, legalProcess.ClientId);
        Assert.Equal("Existing Process", legalProcess.Title);
        Assert.Equal(CreatedAt, legalProcess.CreatedAt);
        Assert.Equal(graph.LegalDeadline.Id, legalDeadline.Id);
        Assert.Equal(graph.Organization.Id, legalDeadline.OrganizationId);
        Assert.Equal(graph.LegalProcess.Id, legalDeadline.ProcessId);
        Assert.Equal("Existing Deadline", legalDeadline.Title);
        Assert.Equal(new DateOnly(2026, 11, 1), legalDeadline.DueDate);
        Assert.Equal(CreatedAt, legalDeadline.CreatedAt);
        Assert.Equal(deadlineCompletedAt, legalDeadline.CompletedAt);

        LegalTask legalTask = CreateLegalTask(graph);
        dbContext.LegalTasks.Add(legalTask);
        await dbContext.SaveChangesAsync();
        Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task<string?> GetConstraintColumnsAsync(string constraintName)
    {
        const string Query =
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = 'legal_tasks'
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = 'FOREIGN KEY'
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string[]> GetPublicTablesAsync()
    {
        const string Query =
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            ORDER BY table_name
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

    private static LegalTask CreateLegalTask(RepresentativeGraph graph)
    {
        return new LegalTask(
            graph.Organization.Id,
            "Migrated Schema Task",
            "Task persistence is available",
            new DateOnly(2028, 2, 29),
            graph.LegalProcess.Id,
            graph.AssigneeMembership.Id,
            graph.CreatorMembership.Id,
            CreatedAt.AddMinutes(1));
    }

    private static object[] GetGraphEntities(RepresentativeGraph graph)
    {
        return
        [
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership,
            graph.AssigneeUser,
            graph.AssigneeMembership,
            graph.Client,
            graph.LegalProcess,
            graph.LegalDeadline
        ];
    }

    private static RepresentativeGraph CreateRepresentativeGraph()
    {
        var organization = new Organization(
            "Enma Legal",
            "enma-legal",
            CreatedAt);
        var creatorUser = new User(
            "Creator User",
            "creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var assigneeUser = new User(
            "Assignee User",
            "assignee@example.test",
            CreatedAt);
        var assigneeMembership = new OrganizationMembership(
            organization.Id,
            assigneeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            "Existing Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Existing Process",
            CreatedAt);
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "Existing Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);

        return new RepresentativeGraph(
            organization,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            legalProcess,
            legalDeadline);
    }

    private sealed record RepresentativeGraph(
        Organization Organization,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
        LegalProcess LegalProcess,
        LegalDeadline LegalDeadline);
}
