using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Documents;
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
public sealed class CalendarEventMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260819180740_AddLegalDocuments";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        22,
        14,
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
        CalendarEvent calendarEvent = CreateCalendarEvent(graph);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(
                GetGraphEntities(graph)
                    .Append(calendarEvent));
            await dbContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        CalendarEvent persistedEvent = await verificationContext.CalendarEvents
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(calendarEvent.Id, persistedEvent.Id);
        Assert.Equal(graph.Organization.Id, persistedEvent.OrganizationId);
        Assert.Equal(graph.LegalProcess.Id, persistedEvent.ProcessId);
        Assert.Equal(
            graph.AssigneeMembership.Id,
            persistedEvent.AssigneeMembershipId);
        Assert.Equal(
            graph.CreatorMembership.Id,
            persistedEvent.CreatedByMembershipId);

        Assert.Equal(
            "organization_id,client_id",
            await GetConstraintColumnsAsync(
                "fk_calendar_events_clients_organization_id_client_id"));
        Assert.Equal(
            "organization_id,process_id",
            await GetConstraintColumnsAsync(
                "fk_calendar_events_processes_organization_id_process_id"));
        Assert.Equal(
            "organization_id,assignee_membership_id",
            await GetConstraintColumnsAsync(
                "fk_calendar_events_memberships_org_assignee_membership_id"));
        Assert.Equal(
            "organization_id,created_by_membership_id",
            await GetConstraintColumnsAsync(
                "fk_calendar_events_memberships_org_created_by_membership_id"));
    }

    [Fact]
    public async Task MigrateAsync_FromPreviousLatestSchema_PreservesExistingData()
    {
        await MigrateAsync(PreviousMigration);
        RepresentativeGraph graph = CreateRepresentativeGraph();

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
                .Append("calendar_events")
                .Append("notifications")
                .Append("audit_logs")
                .Append("organization_invitations")
                .OrderBy(table => table),
            tablesAfter);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        Client client = await dbContext.Clients.AsNoTracking().SingleAsync();
        LegalProcess legalProcess = await dbContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync();
        LegalDeadline legalDeadline = await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync();
        LegalTask legalTask = await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync();
        LegalDocument legalDocument = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync();
        OrganizationMembership[] memberships =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .OrderBy(membership => membership.Id)
                .ToArrayAsync();
        User[] users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.Id)
            .ToArrayAsync();

        Assert.Equal(graph.Organization.Id, organization.Id);
        Assert.Equal("Existing Organization", organization.Name);
        Assert.Equal(graph.Client.Id, client.Id);
        Assert.Equal("Existing Client", client.Name);
        Assert.Equal(graph.LegalProcess.Id, legalProcess.Id);
        Assert.Equal("Existing Process", legalProcess.Title);
        Assert.Equal(graph.LegalDeadline.Id, legalDeadline.Id);
        Assert.Equal(new DateOnly(2026, 9, 30), legalDeadline.DueDate);
        Assert.Equal(graph.LegalTask.Id, legalTask.Id);
        Assert.Equal(new DateOnly(2026, 9, 15), legalTask.DueDate);
        Assert.Equal(graph.LegalDocument.Id, legalDocument.Id);
        Assert.Equal("existing.pdf", legalDocument.OriginalFileName);
        Assert.Equal(2, memberships.Length);
        Assert.Equal(2, users.Length);

        CalendarEvent calendarEvent = CreateCalendarEvent(graph);
        dbContext.CalendarEvents.Add(calendarEvent);
        await dbContext.SaveChangesAsync();
        Assert.Equal(1, await dbContext.CalendarEvents.CountAsync());
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
              AND tc.table_name = 'calendar_events'
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

    private static CalendarEvent CreateCalendarEvent(RepresentativeGraph graph)
    {
        DateTimeOffset startsAt = CreatedAt.AddDays(2);

        return new CalendarEvent(
            graph.Organization.Id,
            "Migrated Calendar Event",
            "Calendar event persistence is available",
            startsAt,
            startsAt.AddHours(2),
            "Hearing Room 2",
            null,
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
            graph.LegalDeadline,
            graph.LegalTask,
            graph.LegalDocument
        ];
    }

    private static RepresentativeGraph CreateRepresentativeGraph()
    {
        var organization = new Organization(
            "Existing Organization",
            "existing-organization",
            CreatedAt);
        var creatorUser = new User(
            "Existing Creator",
            "existing.creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var assigneeUser = new User(
            "Existing Assignee",
            "existing.assignee@example.test",
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
            new DateOnly(2026, 9, 30),
            CreatedAt);
        var legalTask = new LegalTask(
            organization.Id,
            "Existing Task",
            "Existing task details",
            new DateOnly(2026, 9, 15),
            legalProcess.Id,
            assigneeMembership.Id,
            creatorMembership.Id,
            CreatedAt);
        var legalDocument = new LegalDocument(
            organization.Id,
            null,
            legalProcess.Id,
            "existing.pdf",
            new string('a', 32),
            "application/pdf",
            1_024,
            new LegalDocumentContentHash(
                Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            creatorMembership.Id,
            CreatedAt);

        return new RepresentativeGraph(
            organization,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            legalProcess,
            legalDeadline,
            legalTask,
            legalDocument);
    }

    private sealed record RepresentativeGraph(
        Organization Organization,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
        LegalProcess LegalProcess,
        LegalDeadline LegalDeadline,
        LegalTask LegalTask,
        LegalDocument LegalDocument);
}
