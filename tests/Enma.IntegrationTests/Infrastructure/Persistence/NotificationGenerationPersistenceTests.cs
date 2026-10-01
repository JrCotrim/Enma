using System.Data.Common;
using Enma.Application.Notifications;
using Enma.Application.Time;
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
using Enma.Infrastructure.Persistence.Queries;
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
    public async Task DeadlineGeneration_AvailableMemberResponsible_IsOnlyRecipient()
    {
        TenantGraph tenant = CreateTenant("deadline-member-responsible");
        _ = AddPerson(tenant, "owner", OrganizationRole.Owner);
        _ = AddPerson(tenant, "administrator", OrganizationRole.Administrator);
        Person responsible = AddPerson(
            tenant,
            "responsible",
            OrganizationRole.Member);
        _ = AddPerson(tenant, "other-member", OrganizationRole.Member);
        LegalDeadline deadline = CreateDeadline(
            tenant,
            "Member responsible",
            SchedulerDate,
            responsible.Membership);
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult first = await GenerateDeadlinesAsync();
        NotificationGenerationSourceResult repeated = await GenerateDeadlinesAsync();
        Notification notification = Assert.Single(await ReadNotificationsAsync());

        Assert.Equal(new NotificationGenerationSourceResult(1, 1), first);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), repeated);
        AssertDeadlineNotification(
            notification,
            tenant,
            deadline,
            responsible.User.Id);
    }

    [Fact]
    public async Task DeadlineGeneration_AvailableOwnerResponsible_ReceivesSingleNotification()
    {
        TenantGraph tenant = CreateTenant("deadline-owner-responsible");
        Person owner = AddPerson(tenant, "owner", OrganizationRole.Owner);
        _ = AddPerson(tenant, "second-owner", OrganizationRole.Owner);
        _ = AddPerson(tenant, "administrator", OrganizationRole.Administrator);
        LegalDeadline deadline = CreateDeadline(
            tenant,
            "Owner responsible",
            SchedulerDate,
            owner.Membership);
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult result = await GenerateDeadlinesAsync();
        Notification notification = Assert.Single(await ReadNotificationsAsync());

        Assert.Equal(new NotificationGenerationSourceResult(1, 1), result);
        AssertDeadlineNotification(
            notification,
            tenant,
            deadline,
            owner.User.Id);
    }

    [Fact]
    public async Task DeadlineGeneration_UnavailableOrMissingResponsible_FallsBackToPrivilegedRecipients()
    {
        TenantGraph tenant = CreateTenant("deadline-responsible-fallback");
        Person owner = AddPerson(tenant, "owner", OrganizationRole.Owner);
        Person administrator = AddPerson(
            tenant,
            "administrator",
            OrganizationRole.Administrator);
        _ = AddPerson(tenant, "member", OrganizationRole.Member);
        Person inactiveMembership = AddPerson(
            tenant,
            "inactive-membership",
            OrganizationRole.Member);
        inactiveMembership.Membership.Deactivate();
        Person inactiveUser = AddPerson(
            tenant,
            "inactive-user",
            OrganizationRole.Member);
        inactiveUser.User.Deactivate();
        LegalDeadline withoutResponsible = CreateDeadline(
            tenant,
            "Without responsible",
            SchedulerDate);
        LegalDeadline inactiveMembershipResponsible = CreateDeadline(
            tenant,
            "Inactive membership responsible",
            SchedulerDate,
            inactiveMembership.Membership);
        LegalDeadline inactiveUserResponsible = CreateDeadline(
            tenant,
            "Inactive user responsible",
            SchedulerDate.AddDays(1),
            inactiveUser.Membership);
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult first = await GenerateDeadlinesAsync();
        NotificationGenerationSourceResult repeated = await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(6, 1), first);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), repeated);
        Assert.Equal(
            new[]
            {
                withoutResponsible.Id,
                inactiveMembershipResponsible.Id,
                inactiveUserResponsible.Id
            }
                .SelectMany(deadlineId => new[]
                {
                    (DeadlineId: deadlineId, RecipientUserId: owner.User.Id),
                    (DeadlineId: deadlineId, RecipientUserId: administrator.User.Id)
                })
                .OrderBy(value => value.DeadlineId)
                .ThenBy(value => value.RecipientUserId),
            notifications
                .Select(notification => (
                    DeadlineId: notification.LegalDeadlineId!.Value,
                    notification.RecipientUserId))
                .OrderBy(value => value.DeadlineId)
                .ThenBy(value => value.RecipientUserId));
    }

    [Fact]
    public async Task DeadlineGeneration_WithResponsible_SkipsInactiveOrganizationCompletedAndOutOfWindow()
    {
        TenantGraph tenant = CreateTenant("deadline-responsible-window");
        _ = AddPerson(tenant, "owner", OrganizationRole.Owner);
        Person responsible = AddPerson(
            tenant,
            "responsible",
            OrganizationRole.Member);
        _ = CreateDeadline(
            tenant,
            "Yesterday",
            SchedulerDate.AddDays(-1),
            responsible.Membership);
        LegalDeadline today = CreateDeadline(
            tenant,
            "Today",
            SchedulerDate,
            responsible.Membership);
        LegalDeadline tomorrow = CreateDeadline(
            tenant,
            "Tomorrow",
            SchedulerDate.AddDays(1),
            responsible.Membership);
        _ = CreateDeadline(
            tenant,
            "After tomorrow",
            SchedulerDate.AddDays(2),
            responsible.Membership);
        LegalDeadline completed = CreateDeadline(
            tenant,
            "Completed",
            SchedulerDate,
            responsible.Membership);
        completed.Complete(GeneratedAt);

        TenantGraph inactiveTenant = CreateTenant("deadline-responsible-inactive-org");
        _ = AddPerson(inactiveTenant, "owner", OrganizationRole.Owner);
        Person inactiveTenantResponsible = AddPerson(
            inactiveTenant,
            "responsible",
            OrganizationRole.Member);
        _ = CreateDeadline(
            inactiveTenant,
            "Inactive organization",
            SchedulerDate,
            inactiveTenantResponsible.Membership);
        inactiveTenant.Organization.Deactivate();

        await SeedAsync(tenant.Entities.Concat(inactiveTenant.Entities));

        NotificationGenerationSourceResult result = await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(2, 1), result);
        Assert.Equal(
            new[]
            {
                (DeadlineId: today.Id, OccurrenceDate: SchedulerDate),
                (DeadlineId: tomorrow.Id, OccurrenceDate: SchedulerDate.AddDays(1))
            }.OrderBy(value => value.DeadlineId),
            notifications
                .Select(notification => (
                    DeadlineId: notification.LegalDeadlineId!.Value,
                    OccurrenceDate: notification.OccurrenceDate!.Value))
                .OrderBy(value => value.DeadlineId));
        Assert.All(
            notifications,
            notification => Assert.Equal(
                responsible.User.Id,
                notification.RecipientUserId));
    }

    [Fact]
    public async Task DeadlineGeneration_ResponsibleRecipients_DoNotCrossTenants()
    {
        TenantGraph firstTenant = CreateTenant("deadline-responsible-first");
        Person firstOwner = AddPerson(
            firstTenant,
            "owner",
            OrganizationRole.Owner);
        Person sharedInFirst = AddPerson(
            firstTenant,
            "shared",
            OrganizationRole.Member);
        LegalDeadline firstWithResponsible = CreateDeadline(
            firstTenant,
            "First with responsible",
            SchedulerDate,
            sharedInFirst.Membership);
        LegalDeadline firstWithoutResponsible = CreateDeadline(
            firstTenant,
            "First without responsible",
            SchedulerDate);

        TenantGraph secondTenant = CreateTenant("deadline-responsible-second");
        Person secondOwner = AddPerson(
            secondTenant,
            "owner",
            OrganizationRole.Owner);
        var sharedMembershipInSecond = new OrganizationMembership(
            secondTenant.Organization.Id,
            sharedInFirst.User.Id,
            OrganizationRole.Member,
            GeneratedAt.AddDays(-1));
        secondTenant.Entities.Add(sharedMembershipInSecond);
        LegalDeadline secondWithoutResponsible = CreateDeadline(
            secondTenant,
            "Second without responsible",
            SchedulerDate);
        LegalDeadline secondWithResponsible = CreateDeadline(
            secondTenant,
            "Second with responsible",
            SchedulerDate,
            sharedMembershipInSecond);
        sharedMembershipInSecond.Deactivate();

        await SeedAsync(firstTenant.Entities.Concat(secondTenant.Entities));

        await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(
            new[]
            {
                (firstTenant.Organization.Id,
                    firstWithResponsible.Id,
                    sharedInFirst.User.Id),
                (firstTenant.Organization.Id,
                    firstWithoutResponsible.Id,
                    firstOwner.User.Id),
                (secondTenant.Organization.Id,
                    secondWithoutResponsible.Id,
                    secondOwner.User.Id),
                (secondTenant.Organization.Id,
                    secondWithResponsible.Id,
                    secondOwner.User.Id)
            }.Order(),
            notifications
                .Select(notification => (
                    notification.OrganizationId,
                    notification.LegalDeadlineId!.Value,
                    notification.RecipientUserId))
                .Order());
    }

    [Fact]
    public async Task DeadlineGeneration_ResponsibleChange_NotifiesNewResponsibleAndKeepsHistory()
    {
        TenantGraph tenant = CreateTenant("deadline-responsible-change");
        _ = AddPerson(tenant, "owner", OrganizationRole.Owner);
        Person originalResponsible = AddPerson(
            tenant,
            "original-responsible",
            OrganizationRole.Member);
        Person newResponsible = AddPerson(
            tenant,
            "new-responsible",
            OrganizationRole.Member);
        LegalDeadline deadline = CreateDeadline(
            tenant,
            "Responsible change",
            SchedulerDate,
            originalResponsible.Membership);
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult first = await GenerateDeadlinesAsync();
        Notification original = Assert.Single(await ReadNotificationsAsync());

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            LegalDeadline persistedDeadline =
                await updateContext.LegalDeadlines.SingleAsync(
                    candidate => candidate.Id == deadline.Id);
            Assert.True(persistedDeadline.ChangeResponsible(
                newResponsible.Membership.Id));
            await updateContext.SaveChangesAsync();
        }

        NotificationGenerationSourceResult afterChange =
            await GenerateDeadlinesAsync();
        NotificationGenerationSourceResult finalRepeat =
            await GenerateDeadlinesAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(1, 1), first);
        Assert.Equal(new NotificationGenerationSourceResult(1, 1), afterChange);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), finalRepeat);
        Assert.Equal(originalResponsible.User.Id, original.RecipientUserId);
        Assert.Equal(2, notifications.Length);
        Assert.Contains(
            notifications,
            notification => notification.Id == original.Id &&
                notification.RecipientUserId == originalResponsible.User.Id);
        Notification added = Assert.Single(
            notifications,
            notification => notification.Id != original.Id);
        AssertDeadlineNotification(
            added,
            tenant,
            deadline,
            newResponsible.User.Id);
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
    public async Task PaymentInstallmentGeneration_EnforcesEligibilityRecipientsAndTenantIsolation()
    {
        TenantGraph activeTenant = CreateTenant("finance-active");
        Person owner = AddPerson(activeTenant, "owner", OrganizationRole.Owner);
        Person firstAdministrator = AddPerson(
            activeTenant,
            "administrator-one",
            OrganizationRole.Administrator);
        Person secondAdministrator = AddPerson(
            activeTenant,
            "administrator-two",
            OrganizationRole.Administrator);
        _ = AddPerson(activeTenant, "member-one", OrganizationRole.Member);
        _ = AddPerson(activeTenant, "member-two", OrganizationRole.Member);
        _ = AddPerson(activeTenant, "member-three", OrganizationRole.Member);
        Person inactiveMembership = AddPerson(
            activeTenant,
            "inactive-membership",
            OrganizationRole.Administrator);
        inactiveMembership.Membership.Deactivate();
        Person inactiveUser = AddPerson(
            activeTenant,
            "inactive-user",
            OrganizationRole.Owner);
        inactiveUser.User.Deactivate();

        PaymentInstallment dueToday = CreatePaymentPlan(
            activeTenant,
            SchedulerDate).Installments.Single();
        PaymentInstallment overdue = CreatePaymentPlan(
            activeTenant,
            SchedulerDate.AddDays(-1)).Installments.Single();
        PaymentInstallment future = CreatePaymentPlan(
            activeTenant,
            SchedulerDate.AddDays(1)).Installments.Single();
        PaymentInstallment paid = CreatePaymentPlan(
            activeTenant,
            SchedulerDate).Installments.Single();
        paid.MarkPaid(GeneratedAt);

        TenantGraph inactiveTenant = CreateTenant("finance-inactive");
        _ = AddPerson(inactiveTenant, "owner", OrganizationRole.Owner);
        PaymentInstallment inactiveOrganizationInstallment = CreatePaymentPlan(
            inactiveTenant,
            SchedulerDate).Installments.Single();
        inactiveTenant.Organization.Deactivate();

        TenantGraph otherTenant = CreateTenant("finance-other");
        Person otherOwner = AddPerson(
            otherTenant,
            "owner",
            OrganizationRole.Owner);
        PaymentInstallment otherInstallment = CreatePaymentPlan(
            otherTenant,
            SchedulerDate).Installments.Single();
        activeTenant.Entities.Add(
            new OrganizationMembership(
                activeTenant.Organization.Id,
                otherOwner.User.Id,
                OrganizationRole.Member,
                GeneratedAt.AddDays(-1)));

        await SeedAsync(
            activeTenant.Entities
                .Concat(inactiveTenant.Entities)
                .Concat(otherTenant.Entities));

        NotificationGenerationSourceResult result =
            await GeneratePaymentInstallmentsAsync();
        Notification[] notifications = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(4, 1), result);
        Assert.Equal(
            new[]
            {
                owner.User.Id,
                firstAdministrator.User.Id,
                secondAdministrator.User.Id
            }.Order(),
            notifications
                .Where(notification =>
                    notification.PaymentInstallmentId == dueToday.Id)
                .Select(notification => notification.RecipientUserId)
                .Order());
        Assert.Contains(
            notifications,
            notification =>
                notification.OrganizationId == otherTenant.Organization.Id &&
                notification.PaymentInstallmentId == otherInstallment.Id &&
                notification.RecipientUserId == otherOwner.User.Id);
        Assert.DoesNotContain(
            notifications,
            notification =>
                notification.PaymentInstallmentId == overdue.Id ||
                notification.PaymentInstallmentId == future.Id ||
                notification.PaymentInstallmentId == paid.Id ||
                notification.PaymentInstallmentId ==
                    inactiveOrganizationInstallment.Id ||
                notification.RecipientUserId == inactiveMembership.User.Id ||
                notification.RecipientUserId == inactiveUser.User.Id ||
                (notification.OrganizationId == activeTenant.Organization.Id &&
                    notification.RecipientUserId == otherOwner.User.Id));
        Assert.All(
            notifications,
            notification =>
            {
                Assert.Equal(
                    NotificationKind.PaymentInstallmentDueToday,
                    notification.Kind);
                Assert.Equal(SchedulerDate, notification.OccurrenceDate);
                Assert.Null(notification.OccurrenceAt);
                Assert.Null(notification.LegalDeadlineId);
                Assert.Null(notification.LegalTaskId);
                Assert.Null(notification.CalendarEventId);
                Assert.Equal(GeneratedAt, notification.GeneratedAt);
                Assert.Null(notification.ReadAt);
            });
    }

    [Fact]
    public async Task PaymentInstallmentGeneration_IsIdempotentAndPreservesPaidHistory()
    {
        TenantGraph tenant = CreateTenant("finance-history");
        Person owner = AddPerson(tenant, "owner", OrganizationRole.Owner);
        PaymentInstallment installment = CreatePaymentPlan(
            tenant,
            SchedulerDate).Installments.Single();
        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult first =
            await GeneratePaymentInstallmentsAsync();
        NotificationGenerationSourceResult repeated =
            await GeneratePaymentInstallmentsAsync();
        Notification original = Assert.Single(await ReadNotificationsAsync());

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            PaymentInstallment persistedInstallment =
                await updateContext.PaymentInstallments.SingleAsync(
                    candidate => candidate.Id == installment.Id);
            persistedInstallment.MarkPaid(GeneratedAt.AddMinutes(1));
            await updateContext.SaveChangesAsync();
        }

        NotificationGenerationSourceResult afterPayment =
            await GeneratePaymentInstallmentsAsync();
        Notification historical = Assert.Single(await ReadNotificationsAsync());

        Assert.Equal(new NotificationGenerationSourceResult(1, 1), first);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), repeated);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), afterPayment);
        Assert.Equal(original.Id, historical.Id);
        Assert.Equal(installment.Id, historical.PaymentInstallmentId);
        Assert.Equal(owner.User.Id, historical.RecipientUserId);
    }

    [Fact]
    public async Task ConcurrentPaymentInstallmentGeneration_DoesNotDuplicate()
    {
        TenantGraph tenant = CreateTenant("finance-concurrent");
        Person owner = AddPerson(tenant, "owner", OrganizationRole.Owner);
        PaymentInstallment installment = CreatePaymentPlan(
            tenant,
            SchedulerDate).Installments.Single();
        await SeedAsync(tenant.Entities);

        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        var firstPersistence = new NotificationGenerationPersistence(firstContext);
        var secondPersistence = new NotificationGenerationPersistence(secondContext);

        NotificationGenerationSourceResult[] results = await Task.WhenAll(
            firstPersistence.GeneratePaymentInstallmentDueTodayAsync(
                SchedulerDate,
                GeneratedAt,
                CancellationToken.None),
            secondPersistence.GeneratePaymentInstallmentDueTodayAsync(
                SchedulerDate,
                GeneratedAt,
                CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.InsertedCount));
        Notification notification = Assert.Single(await ReadNotificationsAsync());
        Assert.Equal(installment.Id, notification.PaymentInstallmentId);
        Assert.Equal(owner.User.Id, notification.RecipientUserId);
    }

    [Fact]
    public async Task PaymentInstallmentGeneration_ProcessesMultipleBatches()
    {
        TenantGraph tenant = CreateTenant("finance-batching");
        _ = AddPerson(tenant, "owner", OrganizationRole.Owner);

        for (int index = 0; index < NotificationGenerationPersistence.BatchSize + 1;
             index++)
        {
            _ = CreatePaymentPlan(tenant, SchedulerDate);
        }

        await SeedAsync(tenant.Entities);

        NotificationGenerationSourceResult result =
            await GeneratePaymentInstallmentsAsync();

        Assert.Equal(
            new NotificationGenerationSourceResult(
                NotificationGenerationPersistence.BatchSize + 1,
                2),
            result);
        Assert.Equal(
            NotificationGenerationPersistence.BatchSize + 1,
            (await ReadNotificationsAsync()).Length);
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
    public async Task DismissedNotification_RemainsDeduplicatedAndOutsideFeed()
    {
        TenantGraph tenant = CreateTenant("dismissed-dedupe");
        Person creator = AddPerson(tenant, "creator", OrganizationRole.Member);
        CreateTask(
            tenant,
            "Dismissed task",
            SchedulerDate,
            creator.Membership);
        await SeedAsync(tenant.Entities);

        Assert.Equal(1, (await GenerateTasksAsync()).InsertedCount);
        Notification notification = Assert.Single(await ReadNotificationsAsync());
        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            var mutations = new NotificationMutationPersistence(mutationContext);
            Assert.True(await mutations.DismissAsync(
                notification.Id,
                tenant.Organization.Id,
                creator.User.Id,
                GeneratedAt.AddMinutes(1)));
        }

        Assert.Equal(0, (await GenerateTasksAsync()).InsertedCount);
        Assert.Single(await ReadNotificationsAsync());
        await using EnmaDbContext readContext = fixture.CreateDbContext();
        var queries = new NotificationReadQueries(readContext);
        NotificationFeedReadResult feed = await queries.ReadFeedAsync(
            tenant.Organization.Id,
            creator.User.Id,
            20);
        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.UnreadCount);
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

    [Fact]
    public async Task GenerateNotificationsUseCase_NearOperationalMidnight_UsesSaoPauloDate()
    {
        // 2026-08-25T02:30Z is 23:30 on 2026-08-24 in America/Sao_Paulo.
        DateOnly operationalToday = new(2026, 8, 24);
        TenantGraph tenant = CreateTenant("operational-midnight");
        Person owner = AddPerson(tenant, "owner", OrganizationRole.Owner);
        _ = CreateDeadline(tenant, "Yesterday", operationalToday.AddDays(-1));
        LegalDeadline today = CreateDeadline(tenant, "Today", operationalToday);
        LegalDeadline tomorrow = CreateDeadline(
            tenant,
            "Tomorrow",
            operationalToday.AddDays(1));
        LegalDeadline afterTomorrow = CreateDeadline(
            tenant,
            "After tomorrow",
            operationalToday.AddDays(2));
        LegalTask todayTask = CreateTask(
            tenant,
            "Today task",
            operationalToday,
            owner.Membership);
        PaymentInstallment dueToday = CreatePaymentPlan(
            tenant,
            operationalToday).Installments.Single();
        PaymentInstallment dueTomorrow = CreatePaymentPlan(
            tenant,
            operationalToday.AddDays(1)).Installments.Single();
        await SeedAsync(tenant.Entities);

        NotificationGenerationCycleResult beforeMidnight =
            await RunGenerationCycleAsync(
                DateTimeOffset.Parse("2026-08-25T02:30:00Z"));
        Notification[] afterFirstCycle = await ReadNotificationsAsync();

        Assert.Equal(new NotificationGenerationSourceResult(2, 1), beforeMidnight.LegalDeadlines);
        Assert.Equal(new NotificationGenerationSourceResult(1, 1), beforeMidnight.LegalTasks);
        Assert.Equal(
            new NotificationGenerationSourceResult(1, 1),
            beforeMidnight.PaymentInstallments);
        Assert.Equal(
            new[] { (today.Id, operationalToday), (tomorrow.Id, operationalToday.AddDays(1)) }
                .OrderBy(value => value.Item1),
            afterFirstCycle
                .Where(notification => notification.Kind == NotificationKind.LegalDeadlineDueSoon)
                .Select(notification => (
                    notification.LegalDeadlineId!.Value,
                    notification.OccurrenceDate!.Value))
                .OrderBy(value => value.Item1));
        Notification taskNotification = Assert.Single(
            afterFirstCycle,
            notification => notification.Kind == NotificationKind.LegalTaskDueSoon);
        Assert.Equal(todayTask.Id, taskNotification.LegalTaskId);
        Assert.Equal(operationalToday, taskNotification.OccurrenceDate);
        Notification installmentNotification = Assert.Single(
            afterFirstCycle,
            notification => notification.Kind == NotificationKind.PaymentInstallmentDueToday);
        Assert.Equal(dueToday.Id, installmentNotification.PaymentInstallmentId);
        Assert.Equal(operationalToday, installmentNotification.OccurrenceDate);

        // 00:30 on 2026-08-25 in America/Sao_Paulo: the window moves by one
        // operational day and existing reminders stay deduplicated.
        NotificationGenerationCycleResult afterMidnight =
            await RunGenerationCycleAsync(
                DateTimeOffset.Parse("2026-08-25T03:30:00Z"));
        Notification[] afterSecondCycle = await ReadNotificationsAsync();
        Notification[] newNotifications = afterSecondCycle
            .ExceptBy(
                afterFirstCycle.Select(notification => notification.Id),
                notification => notification.Id)
            .ToArray();

        Assert.Equal(new NotificationGenerationSourceResult(1, 1), afterMidnight.LegalDeadlines);
        Assert.Equal(new NotificationGenerationSourceResult(0, 1), afterMidnight.LegalTasks);
        Assert.Equal(
            new NotificationGenerationSourceResult(1, 1),
            afterMidnight.PaymentInstallments);
        Assert.Equal(2, newNotifications.Length);
        Assert.Contains(
            newNotifications,
            notification => notification.LegalDeadlineId == afterTomorrow.Id &&
                notification.OccurrenceDate == operationalToday.AddDays(2));
        Assert.Contains(
            newNotifications,
            notification => notification.PaymentInstallmentId == dueTomorrow.Id &&
                notification.OccurrenceDate == operationalToday.AddDays(1));
    }

    private async Task<NotificationGenerationCycleResult> RunGenerationCycleAsync(
        DateTimeOffset utcNow)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new GenerateNotificationsUseCase(
            new NotificationGenerationPersistence(dbContext),
            new OperationalCalendar(
                new FixedTimeProvider(utcNow),
                TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")));
        return await useCase.ExecuteAsync();
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

    private async Task<NotificationGenerationSourceResult>
        GeneratePaymentInstallmentsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationGenerationPersistence(dbContext);
        return await persistence.GeneratePaymentInstallmentDueTodayAsync(
            SchedulerDate,
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
        DateOnly dueDate,
        OrganizationMembership? responsible = null)
    {
        var deadline = new LegalDeadline(
            tenant.Organization.Id,
            tenant.LegalProcess.Id,
            title,
            dueDate,
            GeneratedAt.AddDays(-1),
            responsible?.Id);
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

    private static ClientPaymentPlan CreatePaymentPlan(
        TenantGraph tenant,
        DateOnly dueDate)
    {
        var paymentPlan = new ClientPaymentPlan(
            tenant.Organization.Id,
            tenant.Client.Id,
            1m,
            1,
            dueDate,
            GeneratedAt.AddDays(-1));
        tenant.Entities.Add(paymentPlan);
        return paymentPlan;
    }

    private static void AssertDeadlineNotification(
        Notification notification,
        TenantGraph tenant,
        LegalDeadline deadline,
        Guid recipientUserId)
    {
        Assert.Equal(tenant.Organization.Id, notification.OrganizationId);
        Assert.Equal(NotificationKind.LegalDeadlineDueSoon, notification.Kind);
        Assert.Equal(deadline.Id, notification.LegalDeadlineId);
        Assert.Equal(recipientUserId, notification.RecipientUserId);
        Assert.Equal(deadline.DueDate, notification.OccurrenceDate);
        Assert.Null(notification.OccurrenceAt);
        Assert.Null(notification.LegalTaskId);
        Assert.Null(notification.CalendarEventId);
        Assert.Null(notification.PaymentInstallmentId);
        Assert.Equal(GeneratedAt, notification.GeneratedAt);
        Assert.Null(notification.ReadAt);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
