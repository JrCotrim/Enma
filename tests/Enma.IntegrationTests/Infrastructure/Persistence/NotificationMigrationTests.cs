using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Notifications;
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
public sealed class NotificationMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260822154734_AddCalendarEvents";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        24,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateOnly DueDate = new(2026, 9, 15);
    private static readonly DateTimeOffset StartsAt = CreatedAt.AddDays(10);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrateAsync_FromPreviousSchema_PreservesDataAndCreatesUsableNotifications()
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

        Assert.Equal(
            tablesBefore.Append("notifications").OrderBy(table => table),
            await GetPublicTablesAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(graph.Organization.Id, (await dbContext.Organizations.SingleAsync()).Id);
        Assert.Equal(
            graph.RecipientMembership.Id,
            (await dbContext.OrganizationMemberships.SingleAsync()).Id);
        Assert.Equal(
            graph.LegalDeadline.Id,
            (await dbContext.LegalDeadlines.SingleAsync()).Id);
        Assert.Equal(graph.LegalTask.Id, (await dbContext.LegalTasks.SingleAsync()).Id);
        Assert.Equal(
            graph.CalendarEvent.Id,
            (await dbContext.CalendarEvents.SingleAsync()).Id);

        dbContext.Notifications.AddRange(
            new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.LegalDeadlineDueSoon,
                graph.LegalDeadline.Id,
                null,
                null,
                graph.LegalDeadline.DueDate,
                null,
                CreatedAt.AddDays(1)),
            new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.LegalTaskDueSoon,
                null,
                graph.LegalTask.Id,
                null,
                graph.LegalTask.DueDate,
                null,
                CreatedAt.AddDays(1)),
            new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.CalendarEventStartingSoon,
                null,
                null,
                graph.CalendarEvent.Id,
                null,
                graph.CalendarEvent.StartsAt,
                CreatedAt.AddDays(1)));
        await dbContext.SaveChangesAsync();

        Assert.Equal(3, await dbContext.Notifications.CountAsync());
        Assert.Equal(
            "organization_id,user_id",
            await GetUniqueConstraintColumnsAsync(
                "organization_memberships",
                "ux_organization_memberships_organization_id_user_id"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "legal_deadlines",
                "ak_legal_deadlines_organization_id_id"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "legal_tasks",
                "ak_legal_tasks_organization_id_id"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "calendar_events",
                "ak_calendar_events_organization_id_id"));
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
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

    private async Task<string?> GetUniqueConstraintColumnsAsync(
        string tableName,
        string constraintName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = 'UNIQUE'
            """,
            connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private static RepresentativeGraph CreateRepresentativeGraph()
    {
        var organization = new Organization(
            "Existing Notification Tenant",
            "existing-notification-tenant",
            CreatedAt);
        var recipientUser = new User(
            "Existing Recipient",
            "existing.notification.recipient@example.test",
            CreatedAt);
        var recipientMembership = new OrganizationMembership(
            organization.Id,
            recipientUser.Id,
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
            DueDate,
            CreatedAt);
        var legalTask = new LegalTask(
            organization.Id,
            "Existing Task",
            null,
            DueDate,
            legalProcess.Id,
            null,
            recipientMembership.Id,
            CreatedAt);
        var calendarEvent = new CalendarEvent(
            organization.Id,
            "Existing Event",
            null,
            StartsAt,
            StartsAt.AddHours(1),
            null,
            null,
            null,
            null,
            recipientMembership.Id,
            CreatedAt);

        return new RepresentativeGraph(
            organization,
            recipientUser,
            recipientMembership,
            client,
            legalProcess,
            legalDeadline,
            legalTask,
            calendarEvent);
    }

    private static object[] GetGraphEntities(RepresentativeGraph graph)
    {
        return
        [
            graph.Organization,
            graph.RecipientUser,
            graph.RecipientMembership,
            graph.Client,
            graph.LegalProcess,
            graph.LegalDeadline,
            graph.LegalTask,
            graph.CalendarEvent
        ];
    }

    private sealed record RepresentativeGraph(
        Organization Organization,
        User RecipientUser,
        OrganizationMembership RecipientMembership,
        Client Client,
        LegalProcess LegalProcess,
        LegalDeadline LegalDeadline,
        LegalTask LegalTask,
        CalendarEvent CalendarEvent);
}
