using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Finance;
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
    private const string PreFinanceMigration =
        "20260907171157_ExtendAuditTaxonomyForFinance";
    private const string FinanceMigration =
        "20260910222135_AddFinancePaymentInstallmentNotifications";

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
            seedContext.AddRange(
                graph.Organization,
                graph.RecipientUser,
                graph.RecipientMembership);
            await seedContext.SaveChangesAsync();
            await PostgreSqlFixture.InsertClientWithoutProfileColumnsAsync(
                seedContext,
                graph.Client);
            seedContext.AddRange(
                graph.LegalProcess,
                graph.LegalDeadline,
                graph.LegalTask,
                graph.CalendarEvent);
            await seedContext.SaveChangesAsync();
        }

        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync();

        Assert.Equal(
            tablesBefore
                .Append("notifications")
                .Append("audit_logs")
                .Append("organization_invitations")
                .Append("client_payment_plans")
                .Append("payment_installments")
                .OrderBy(table => table),
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

        var paymentPlan = new ClientPaymentPlan(
            graph.Organization.Id,
            graph.Client.Id,
            100m,
            1,
            DueDate,
            CreatedAt.AddDays(1));
        dbContext.ClientPaymentPlans.Add(paymentPlan);
        await dbContext.SaveChangesAsync();
        PaymentInstallment installment = Assert.Single(paymentPlan.Installments);

        dbContext.Notifications.AddRange(
            new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.LegalDeadlineDueSoon,
                graph.LegalDeadline.Id,
                null,
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
                null,
                graph.CalendarEvent.StartsAt,
                CreatedAt.AddDays(1)),
            new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.PaymentInstallmentDueToday,
                null,
                null,
                null,
                installment.Id,
                installment.DueDate,
                null,
                CreatedAt.AddDays(1)));
        await dbContext.SaveChangesAsync();

        Assert.Equal(4, await dbContext.Notifications.CountAsync());
        Assert.Equal(
            installment.Id,
            (await dbContext.Notifications.SingleAsync(notification =>
                notification.Kind ==
                    NotificationKind.PaymentInstallmentDueToday))
                .PaymentInstallmentId);
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

    [Fact]
    public async Task MigrateAsync_DownRemovesOnlyFinanceNotificationsAndReUpgrades()
    {
        RepresentativeGraph graph = CreateRepresentativeGraph();
        var paymentPlan = new ClientPaymentPlan(
            graph.Organization.Id,
            graph.Client.Id,
            100m,
            1,
            DueDate,
            CreatedAt.AddDays(1));

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(GetGraphEntities(graph));
            seedContext.ClientPaymentPlans.Add(paymentPlan);
            await seedContext.SaveChangesAsync();

            PaymentInstallment installment = Assert.Single(paymentPlan.Installments);
            seedContext.Notifications.AddRange(
                new Notification(
                    graph.Organization.Id,
                    graph.RecipientUser.Id,
                    NotificationKind.LegalDeadlineDueSoon,
                    graph.LegalDeadline.Id,
                    null,
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
                    null,
                    graph.CalendarEvent.StartsAt,
                    CreatedAt.AddDays(1)),
                new Notification(
                    graph.Organization.Id,
                    graph.RecipientUser.Id,
                    NotificationKind.PaymentInstallmentDueToday,
                    null,
                    null,
                    null,
                    installment.Id,
                    installment.DueDate,
                    null,
                    CreatedAt.AddDays(1)));
            await seedContext.SaveChangesAsync();
        }

        int[] currentKinds = await GetNotificationKindsAsync();
        Assert.Equal([1, 2, 3, 4], currentKinds);

        await MigrateAsync(PreFinanceMigration);

        int[] downgradedKinds = await GetNotificationKindsAsync();
        Assert.Equal([1, 2, 3], downgradedKinds);
        Assert.False(await NotificationColumnExistsAsync("payment_installment_id"));
        Assert.Equal(
            [
                "ck_notifications_exactly_one_source",
                "ck_notifications_kind",
                "ck_notifications_kind_source",
                "ck_notifications_occurrence",
                "ck_notifications_read_at"
            ],
            await GetNotificationCheckConstraintNamesAsync());

        await MigrateAsync(FinanceMigration);

        int[] reUpgradedKinds = await GetNotificationKindsAsync();
        Assert.Equal([1, 2, 3], reUpgradedKinds);
        Assert.True(await NotificationColumnExistsAsync("payment_installment_id"));

        await using (EnmaDbContext reUpgradedContext = fixture.CreateDbContext())
        {
            PaymentInstallment installment = paymentPlan.Installments.Single();
            reUpgradedContext.Notifications.Add(new Notification(
                graph.Organization.Id,
                graph.RecipientUser.Id,
                NotificationKind.PaymentInstallmentDueToday,
                null,
                null,
                null,
                installment.Id,
                installment.DueDate,
                null,
                CreatedAt.AddDays(2)));
            await reUpgradedContext.SaveChangesAsync();
        }

        int[] healthySchemaKinds = await GetNotificationKindsAsync();
        Assert.Equal([1, 2, 3, 4], healthySchemaKinds);
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

    private async Task<int[]> GetNotificationKindsAsync()
    {
        var kinds = new List<int>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT kind FROM notifications ORDER BY kind",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            kinds.Add(reader.GetInt32(0));
        }

        return kinds.ToArray();
    }

    private async Task<bool> NotificationColumnExistsAsync(string columnName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'notifications'
                  AND column_name = @columnName)
            """,
            connection);
        command.Parameters.AddWithValue("columnName", columnName);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private async Task<string[]> GetNotificationCheckConstraintNamesAsync()
    {
        var names = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'public'
              AND table_name = 'notifications'
              AND constraint_type = 'CHECK'
              AND constraint_name LIKE 'ck_notifications_%'
            ORDER BY constraint_name
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
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
