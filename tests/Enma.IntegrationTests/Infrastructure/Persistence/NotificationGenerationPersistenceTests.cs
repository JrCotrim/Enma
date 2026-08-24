using System.Data.Common;
using Enma.Application.Notifications;
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
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationGenerationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset GeneratedAt = new(
        2026,
        8,
        25,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateOnly SchedulerDate = new(2026, 8, 25);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeadlineGeneration_UsesPrivilegedActiveRecipientsAndDateBoundaries()
    {
        TenantGraph activeTenant = CreateTenant("deadline-active");
        Person owner = AddPerson(activeTenant, "owner", OrganizationRole.Owner);
        Person administrator = AddPerson(
            activeTenant,
            "administrator",
            OrganizationRole.Administrator);
        _ = AddPerson(activeTenant, "member", OrganizationRole.Member);
        Person inactiveAdministrator = AddPerson(
            activeTenant,
            "inactive-administrator",
            OrganizationRole.Administrator);
        inactiveAdministrator.Membership.Deactivate();
        Person inactiveOwnerUser = AddPerson(
            activeTenant,
            "inactive-owner-user",
            OrganizationRole.Owner);
        inactiveOwnerUser.User.Deactivate();

        LegalDeadline today = CreateDeadline(
            activeTenant,
            "Today",
            SchedulerDate);
        LegalDeadline tomorrow = CreateDeadline(
            activeTenant,
            "Tomorrow",
            SchedulerDate.AddDays(1));
        _ = CreateDeadline(
            activeTenant,
            "Yesterday",
            SchedulerDate.AddDays(-1));
        LegalDeadline completed = CreateDeadline(
            activeTenant,
            "Completed",
            SchedulerDate);
        completed.Complete(GeneratedAt);

        TenantGraph inactiveTenant = CreateTenant("deadline-inactive");
        _ = AddPerson(inactiveTenant, "owner", OrganizationRole.Owner);
        _ = CreateDeadline(
            inactiveTenant,
            "Inactive organization",
            SchedulerDate);
        inactiveTenant.Organization.Deactivate();

        await SeedAsync(activeTenant.Entities.Concat(inactiveTenant.Entities));

        NotificationGenerationSourceResult result =
            await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(4, 1), result);
        Assert.All(
            notifications,
            notification => Assert.Equal(GeneratedAt, notification.GeneratedAt));
        Assert.Equal(
            new[]
            {
                (DeadlineId: today.Id,
                    RecipientUserId: owner.User.Id,
                    OccurrenceDate: SchedulerDate),
                (DeadlineId: today.Id,
                    RecipientUserId: administrator.User.Id,
                    OccurrenceDate: SchedulerDate),
                (DeadlineId: tomorrow.Id,
                    RecipientUserId: owner.User.Id,
                    OccurrenceDate: SchedulerDate.AddDays(1)),
                (DeadlineId: tomorrow.Id,
                    RecipientUserId: administrator.User.Id,
                    OccurrenceDate: SchedulerDate.AddDays(1))
            }.OrderBy(value => value.DeadlineId)
                .ThenBy(value => value.RecipientUserId),
            notifications
                .Select(notification => (
                    DeadlineId: notification.LegalDeadlineId!.Value,
                    notification.RecipientUserId,
                    OccurrenceDate: notification.OccurrenceDate!.Value))
                .OrderBy(value => value.DeadlineId)
                .ThenBy(value => value.RecipientUserId));
    }

    [Fact]
    public async Task DeadlineGeneration_QualifiesRoleAndRecipientBySourceTenant()
    {
        TenantGraph firstTenant = CreateTenant("tenant-first");
        Person firstOwner = AddPerson(
            firstTenant,
            "first-owner",
            OrganizationRole.Owner);
        LegalDeadline firstDeadline = CreateDeadline(
            firstTenant,
            "First deadline",
            SchedulerDate);

        TenantGraph secondTenant = CreateTenant("tenant-second");
        Person sharedUserInSecond = AddPerson(
            secondTenant,
            "shared",
            OrganizationRole.Owner);
        LegalDeadline secondDeadline = CreateDeadline(
            secondTenant,
            "Second deadline",
            SchedulerDate);
        var sharedUserMembershipInFirst = new OrganizationMembership(
            firstTenant.Organization.Id,
            sharedUserInSecond.User.Id,
            OrganizationRole.Member,
            GeneratedAt.AddDays(-1));
        firstTenant.Entities.Add(sharedUserMembershipInFirst);

        await SeedAsync(firstTenant.Entities.Concat(secondTenant.Entities));

        await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Contains(
            notifications,
            notification =>
                notification.OrganizationId == firstTenant.Organization.Id &&
                notification.LegalDeadlineId == firstDeadline.Id &&
                notification.RecipientUserId == firstOwner.User.Id);
        Assert.Contains(
            notifications,
            notification =>
                notification.OrganizationId == secondTenant.Organization.Id &&
                notification.LegalDeadlineId == secondDeadline.Id &&
                notification.RecipientUserId == sharedUserInSecond.User.Id);
        Assert.DoesNotContain(
            notifications,
            notification =>
                notification.OrganizationId == firstTenant.Organization.Id &&
                notification.RecipientUserId == sharedUserInSecond.User.Id);
        Assert.DoesNotContain(
            notifications,
            notification =>
                notification.OrganizationId != firstTenant.Organization.Id &&
                notification.LegalDeadlineId == firstDeadline.Id);
    }

    [Fact]
    public async Task TaskGeneration_UsesAssigneeOrCreatorWithoutInactiveAssigneeFallback()
    {
        TenantGraph tenant = CreateTenant("tasks");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        Person assignee = AddPerson(tenant, "assignee", OrganizationRole.Member);
        Person inactiveAssignee = AddPerson(
            tenant,
            "inactive-assignee",
            OrganizationRole.Member);
        inactiveAssignee.Membership.Deactivate();
        Person inactiveUserAssignee = AddPerson(
            tenant,
            "inactive-user-assignee",
            OrganizationRole.Member);
        inactiveUserAssignee.User.Deactivate();

        LegalTask assigned = CreateTask(
            tenant,
            "Assigned",
            SchedulerDate,
            creator.Membership,
            assignee.Membership);
        LegalTask unassigned = CreateTask(
            tenant,
            "Unassigned",
            SchedulerDate.AddDays(1),
            creator.Membership);
        _ = CreateTask(
            tenant,
            "Inactive explicit assignee",
            SchedulerDate,
            creator.Membership,
            inactiveAssignee.Membership);
        _ = CreateTask(
            tenant,
            "Inactive explicit assignee user",
            SchedulerDate,
            creator.Membership,
            inactiveUserAssignee.Membership);
        LegalTask completed = CreateTask(
            tenant,
            "Completed",
            SchedulerDate,
            creator.Membership);
        completed.Complete(GeneratedAt);
        _ = CreateTask(
            tenant,
            "No due date",
            null,
            creator.Membership);
        _ = CreateTask(
            tenant,
            "Yesterday",
            SchedulerDate.AddDays(-1),
            creator.Membership);

        TenantGraph inactiveTenant = CreateTenant("tasks-inactive-org");
        Person inactiveTenantCreator = AddPerson(
            inactiveTenant,
            "creator",
            OrganizationRole.Member);
        _ = CreateTask(
            inactiveTenant,
            "Inactive organization",
            SchedulerDate,
            inactiveTenantCreator.Membership);
        inactiveTenant.Organization.Deactivate();

        await SeedAsync(tenant.Entities.Concat(inactiveTenant.Entities));

        NotificationGenerationSourceResult result = await GenerateTasksAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(2, 1), result);
        Assert.Contains(
            notifications,
            notification =>
                notification.LegalTaskId == assigned.Id &&
                notification.RecipientUserId == assignee.User.Id &&
                notification.OccurrenceDate == assigned.DueDate);
        Assert.Contains(
            notifications,
            notification =>
                notification.LegalTaskId == unassigned.Id &&
                notification.RecipientUserId == creator.User.Id &&
                notification.OccurrenceDate == unassigned.DueDate);
        Assert.All(
            notifications,
            notification =>
                AssertTaskNotification(
                    notification,
                    notification.LegalTaskId == assigned.Id
                        ? assigned
                        : unassigned,
                    notification.LegalTaskId == assigned.Id
                        ? assignee.User.Id
                        : creator.User.Id));
        Assert.DoesNotContain(
            notifications,
            notification =>
                notification.RecipientUserId == inactiveAssignee.User.Id ||
                notification.RecipientUserId == inactiveUserAssignee.User.Id);
    }

    [Fact]
    public async Task CalendarEventGeneration_UsesOpenClosedWindowAndNoAssigneeFallback()
    {
        TenantGraph tenant = CreateTenant("events");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        Person assignee = AddPerson(tenant, "assignee", OrganizationRole.Member);
        Person inactiveAssignee = AddPerson(
            tenant,
            "inactive-assignee",
            OrganizationRole.Member);
        inactiveAssignee.Membership.Deactivate();
        Person inactiveUserAssignee = AddPerson(
            tenant,
            "inactive-user-assignee",
            OrganizationRole.Member);
        inactiveUserAssignee.User.Deactivate();

        CalendarEvent assigned = CreateEvent(
            tenant,
            "Assigned",
            GeneratedAt.AddTicks(10),
            creator.Membership,
            assignee.Membership);
        CalendarEvent unassigned = CreateEvent(
            tenant,
            "Unassigned",
            GeneratedAt.AddMinutes(60),
            creator.Membership);
        _ = CreateEvent(
            tenant,
            "Inactive explicit assignee",
            GeneratedAt.AddMinutes(30),
            creator.Membership,
            inactiveAssignee.Membership);
        _ = CreateEvent(
            tenant,
            "Inactive explicit assignee user",
            GeneratedAt.AddMinutes(30),
            creator.Membership,
            inactiveUserAssignee.Membership);
        _ = CreateEvent(
            tenant,
            "Starts now",
            GeneratedAt,
            creator.Membership);
        _ = CreateEvent(
            tenant,
            "Already started",
            GeneratedAt.AddTicks(-10),
            creator.Membership);
        _ = CreateEvent(
            tenant,
            "After window",
            GeneratedAt.AddMinutes(60).AddTicks(10),
            creator.Membership);

        TenantGraph inactiveTenant = CreateTenant("events-inactive-org");
        Person inactiveTenantCreator = AddPerson(
            inactiveTenant,
            "creator",
            OrganizationRole.Member);
        _ = CreateEvent(
            inactiveTenant,
            "Inactive organization",
            GeneratedAt.AddMinutes(30),
            inactiveTenantCreator.Membership);
        inactiveTenant.Organization.Deactivate();

        await SeedAsync(tenant.Entities.Concat(inactiveTenant.Entities));

        NotificationGenerationSourceResult result = await GenerateEventsAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(2, 1), result);
        Assert.Contains(
            notifications,
            notification =>
                notification.CalendarEventId == assigned.Id &&
                notification.RecipientUserId == assignee.User.Id &&
                notification.OccurrenceAt == assigned.StartsAt);
        Assert.Contains(
            notifications,
            notification =>
                notification.CalendarEventId == unassigned.Id &&
                notification.RecipientUserId == creator.User.Id &&
                notification.OccurrenceAt == unassigned.StartsAt);
        Assert.DoesNotContain(
            notifications,
            notification =>
                notification.RecipientUserId == inactiveAssignee.User.Id ||
                notification.RecipientUserId == inactiveUserAssignee.User.Id);
    }

    [Fact]
    public async Task RepeatedGeneration_IsIdempotentAndUsesCurrentAssignment()
    {
        TenantGraph tenant = CreateTenant("assignment-change");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        Person originalAssignee = AddPerson(
            tenant,
            "original-assignee",
            OrganizationRole.Member);
        Person newAssignee = AddPerson(
            tenant,
            "new-assignee",
            OrganizationRole.Member);
        LegalTask task = CreateTask(
            tenant,
            "Reassigned",
            SchedulerDate,
            creator.Membership,
            originalAssignee.Membership);
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult first = await GenerateTasksAsync();
        NotificationGenerationSourceResult repeated = await GenerateTasksAsync();

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            LegalTask persistedTask = await updateContext.LegalTasks.SingleAsync(
                candidate => candidate.Id == task.Id);
            persistedTask.ChangeAssignee(newAssignee.Membership.Id);
            await updateContext.SaveChangesAsync();
        }

        NotificationGenerationSourceResult afterAssignmentChange =
            await GenerateTasksAsync();
        NotificationGenerationSourceResult finalRepeat = await GenerateTasksAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(0, repeated.InsertedCount);
        Assert.Equal(1, afterAssignmentChange.InsertedCount);
        Assert.Equal(0, finalRepeat.InsertedCount);
        Assert.Equal(
            new[] { originalAssignee.User.Id, newAssignee.User.Id }.Order(),
            notifications.Select(notification => notification.RecipientUserId).Order());
    }

    [Fact]
    public async Task ConcurrentGeneration_ReliesOnDedupeConstraintWithoutDuplicates()
    {
        TenantGraph tenant = CreateTenant("concurrent");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        LegalTask task = CreateTask(
            tenant,
            "Concurrent",
            SchedulerDate,
            creator.Membership);
        await SeedAsync(tenant.Entities);

        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        var firstPersistence = new NotificationGenerationPersistence(firstContext);
        var secondPersistence = new NotificationGenerationPersistence(secondContext);

        NotificationGenerationSourceResult[] results = await Task.WhenAll(
            firstPersistence.GenerateLegalTaskRemindersAsync(
                SchedulerDate,
                SchedulerDate.AddDays(1),
                GeneratedAt,
                CancellationToken.None),
            secondPersistence.GenerateLegalTaskRemindersAsync(
                SchedulerDate,
                SchedulerDate.AddDays(1),
                GeneratedAt,
                CancellationToken.None));

        Notification[] notifications = await ReadNotificationsAsync();
        Assert.Equal(1, results.Sum(result => result.InsertedCount));
        Notification notification = Assert.Single(notifications);
        Assert.Equal(task.Id, notification.LegalTaskId);
        Assert.Equal(creator.User.Id, notification.RecipientUserId);
    }

    [Fact]
    public async Task ExactDedupeTarget_DoesNotHideUnrelatedPrimaryKeyFailure()
    {
        Guid fixedNotificationId = Guid.Parse(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        TenantGraph tenant = CreateTenant("integrity");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        LegalTask alreadyGeneratedTask = CreateTask(
            tenant,
            "Existing",
            SchedulerDate,
            creator.Membership);
        _ = CreateTask(
            tenant,
            "Candidate",
            SchedulerDate,
            creator.Membership);
        var existingNotification = new Notification(
            tenant.Organization.Id,
            creator.User.Id,
            NotificationKind.LegalTaskDueSoon,
            null,
            alreadyGeneratedTask.Id,
            null,
            SchedulerDate,
            null,
            GeneratedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(tenant.Entities);
        dbContext.Notifications.Add(existingNotification);
        dbContext.Entry(existingNotification)
            .Property(notification => notification.Id)
            .CurrentValue = fixedNotificationId;
        await dbContext.SaveChangesAsync();
        await dbContext.Database.OpenConnectionAsync();
        await InstallForcedNotificationIdTriggerAsync(
            dbContext,
            fixedNotificationId);
        var persistence = new NotificationGenerationPersistence(dbContext);

        PostgresException exception;

        try
        {
            exception = await Assert.ThrowsAsync<PostgresException>(
                () => persistence.GenerateLegalTaskRemindersAsync(
                    SchedulerDate,
                    SchedulerDate.AddDays(1),
                    GeneratedAt,
                    CancellationToken.None));
        }
        finally
        {
            await RemoveForcedNotificationIdTriggerAsync(dbContext);
        }

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("pk_notifications", exception.ConstraintName);
        Assert.Single(await ReadNotificationsAsync());
    }

    [Fact]
    public async Task Batching_ProgressesPastBatchAndSkipsAlreadyGeneratedCandidates()
    {
        TenantGraph tenant = CreateTenant("batching");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        await SeedAsync(tenant.Entities);
        await SeedBatchTasksAsync(
            tenant.Organization.Id,
            creator.Membership.Id,
            count: 5_001);

        NotificationGenerationSourceResult first = await GenerateTasksAsync();
        NotificationGenerationSourceResult second = await GenerateTasksAsync();
        NotificationGenerationSourceResult third = await GenerateTasksAsync();

        Assert.Equal(new NotificationGenerationSourceResult(5_000, 10), first);
        Assert.Equal(new NotificationGenerationSourceResult(1, 1), second);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), third);
        Assert.Equal(5_001, (await ReadNotificationsAsync()).Length);
    }

    [Fact]
    public async Task HostCancellation_IsNotClassifiedAsTransientFailure()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationGenerationPersistence(dbContext);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Exception exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistence.GenerateLegalTaskRemindersAsync(
                SchedulerDate,
                SchedulerDate.AddDays(1),
                GeneratedAt,
                cancellation.Token));

        Assert.IsNotType<NotificationGenerationTransientException>(exception);
    }

    private async Task<NotificationGenerationSourceResult> GenerateDeadlinesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationGenerationPersistence(dbContext);
        return await persistence.GenerateLegalDeadlineRemindersAsync(
            SchedulerDate,
            SchedulerDate.AddDays(1),
            GeneratedAt,
            CancellationToken.None);
    }

    private async Task<NotificationGenerationSourceResult> GenerateTasksAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationGenerationPersistence(dbContext);
        return await persistence.GenerateLegalTaskRemindersAsync(
            SchedulerDate,
            SchedulerDate.AddDays(1),
            GeneratedAt,
            CancellationToken.None);
    }

    private async Task<NotificationGenerationSourceResult> GenerateEventsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationGenerationPersistence(dbContext);
        return await persistence.GenerateCalendarEventRemindersAsync(
            GeneratedAt,
            GeneratedAt.AddMinutes(60),
            GeneratedAt,
            CancellationToken.None);
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedBatchTasksAsync(
        Guid organizationId,
        Guid creatorMembershipId,
        int count)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO legal_tasks (
                id,
                organization_id,
                title,
                description,
                due_date,
                process_id,
                assignee_membership_id,
                created_by_membership_id,
                created_at,
                completed_at
            )
            SELECT
                gen_random_uuid(),
                {organizationId},
                'Batch task ' || candidate::text,
                NULL,
                {SchedulerDate},
                NULL,
                NULL,
                {creatorMembershipId},
                {GeneratedAt.AddDays(-1)},
                NULL
            FROM generate_series(1, {count}) AS candidate
            """);
    }

    private async Task<Notification[]> ReadNotificationsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Notifications
            .AsNoTracking()
            .OrderBy(notification => notification.Id)
            .ToArrayAsync();
    }

    private static TenantGraph CreateTenant(string key)
    {
        var organization = new Organization(
            $"Organization {key}",
            key,
            GeneratedAt.AddDays(-2));
        var client = new Client(
            organization.Id,
            $"Client {key}",
            GeneratedAt.AddDays(-2));
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"Process {key}",
            GeneratedAt.AddDays(-2));
        return new TenantGraph(organization, client, legalProcess);
    }

    private static Person AddPerson(
        TenantGraph tenant,
        string key,
        OrganizationRole role)
    {
        var user = new User(
            $"User {tenant.Organization.Slug} {key}",
            $"{tenant.Organization.Slug}.{key}@example.test",
            GeneratedAt.AddDays(-2));
        var membership = new OrganizationMembership(
            tenant.Organization.Id,
            user.Id,
            role,
            GeneratedAt.AddDays(-2));
        tenant.Entities.Add(user);
        tenant.Entities.Add(membership);
        return new Person(user, membership);
    }

    private static LegalDeadline CreateDeadline(
        TenantGraph tenant,
        string title,
        DateOnly dueDate)
    {
        var deadline = new LegalDeadline(
            tenant.Organization.Id,
            tenant.LegalProcess.Id,
            title,
            dueDate,
            GeneratedAt.AddDays(-1));
        tenant.Entities.Add(deadline);
        return deadline;
    }

    private static LegalTask CreateTask(
        TenantGraph tenant,
        string title,
        DateOnly? dueDate,
        OrganizationMembership creator,
        OrganizationMembership? assignee = null)
    {
        var legalTask = new LegalTask(
            tenant.Organization.Id,
            title,
            null,
            dueDate,
            null,
            assignee?.Id,
            creator.Id,
            GeneratedAt.AddDays(-1));
        tenant.Entities.Add(legalTask);
        return legalTask;
    }

    private static CalendarEvent CreateEvent(
        TenantGraph tenant,
        string title,
        DateTimeOffset startsAt,
        OrganizationMembership creator,
        OrganizationMembership? assignee = null)
    {
        var calendarEvent = new CalendarEvent(
            tenant.Organization.Id,
            title,
            null,
            startsAt,
            startsAt.AddHours(1),
            null,
            null,
            null,
            assignee?.Id,
            creator.Id,
            GeneratedAt.AddDays(-1));
        tenant.Entities.Add(calendarEvent);
        return calendarEvent;
    }

    private static void AssertTaskNotification(
        Notification notification,
        LegalTask task,
        Guid recipientUserId)
    {
        Assert.Equal(NotificationKind.LegalTaskDueSoon, notification.Kind);
        Assert.Equal(task.Id, notification.LegalTaskId);
        Assert.Equal(recipientUserId, notification.RecipientUserId);
        Assert.Equal(task.DueDate, notification.OccurrenceDate);
        Assert.Equal(GeneratedAt, notification.GeneratedAt);
    }

    private static async Task InstallForcedNotificationIdTriggerAsync(
        EnmaDbContext dbContext,
        Guid fixedNotificationId)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            CREATE FUNCTION pg_temp.force_notification_id()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                NEW.id := '{fixedNotificationId}'::uuid;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER test_force_notification_id
            BEFORE INSERT ON notifications
            FOR EACH ROW
            EXECUTE FUNCTION pg_temp.force_notification_id();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveForcedNotificationIdTriggerAsync(
        EnmaDbContext dbContext)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "DROP TRIGGER test_force_notification_id ON notifications;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TenantGraph
    {
        public TenantGraph(
            Organization organization,
            Client client,
            LegalProcess legalProcess)
        {
            Organization = organization;
            Client = client;
            LegalProcess = legalProcess;
            Entities = [organization, client, legalProcess];
        }

        public Organization Organization { get; }

        public Client Client { get; }

        public LegalProcess LegalProcess { get; }

        public List<object> Entities { get; }
    }

    private sealed record Person(
        User User,
        OrganizationMembership Membership);
}
