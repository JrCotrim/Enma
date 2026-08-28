using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Domain.Auditing;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Documents;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberLifecycleMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        26,
        15,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Owner, OrganizationRole.Member)]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Owner, OrganizationRole.Administrator)]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Administrator, OrganizationRole.Member)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Owner, OrganizationRole.Member)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Owner, OrganizationRole.Administrator)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Administrator, OrganizationRole.Member)]
    public async Task ExecuteAsync_AuthorizedMatrix_ChangesSameMembershipRow(
        OrganizationMemberLifecycleOperation operation,
        OrganizationRole actorRole,
        OrganizationRole targetRole)
    {
        TestGraph graph = await SeedGraphAsync(
            actorRole,
            targetRole,
            targetMembershipActive:
                operation == OrganizationMemberLifecycleOperation.Deactivate);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph, operation));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership persisted = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .SingleAsync(membership => membership.Id == graph.TargetMembership.Id);
        Assert.Equal(graph.TargetMembership.Id, persisted.Id);
        Assert.Equal(
            operation == OrganizationMemberLifecycleOperation.Reactivate,
            persisted.IsActive);
        Assert.Equal(
            1,
            await dbContext.OrganizationMemberships.CountAsync(
                membership => membership.Id == graph.TargetMembership.Id));
        AuditLog auditLog = await dbContext.AuditLogs
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(graph.Organization.Id, auditLog.OrganizationId);
        Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
        Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(actorRole, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(
            operation == OrganizationMemberLifecycleOperation.Deactivate
                ? AuditEventType.OrganizationMembershipDeactivated
                : AuditEventType.OrganizationMembershipReactivated,
            auditLog.EventType);
        Assert.Equal(AuditEntityType.OrganizationMembership, auditLog.EntityType);
        Assert.Equal(graph.TargetMembership.Id, auditLog.EntityId);
        Assert.Equal(Now, auditLog.OccurredAt);
        Assert.Null(auditLog.Details);
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Administrator, OrganizationRole.Administrator)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Administrator, OrganizationRole.Administrator)]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Owner, OrganizationRole.Owner)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Owner, OrganizationRole.Owner)]
    public async Task ExecuteAsync_ForbiddenTargetMatrix_DeniesWithoutWrite(
        OrganizationMemberLifecycleOperation operation,
        OrganizationRole actorRole,
        OrganizationRole targetRole)
    {
        bool initiallyActive =
            operation == OrganizationMemberLifecycleOperation.Deactivate;
        TestGraph graph = await SeedGraphAsync(
            actorRole,
            targetRole,
            initiallyActive);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph, operation));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
            result);
        Assert.Equal(
            initiallyActive,
            await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate)]
    public async Task ExecuteAsync_SelfTarget_Denies(
        OrganizationMemberLifecycleOperation operation)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        OrganizationMemberLifecycleMutationPersistenceRequest request =
            CreateRequest(graph, operation) with
            {
                TargetMembershipId = graph.ActorMembership.Id
            };

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(request);

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
            result);
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate)]
    public async Task ExecuteAsync_ForeignAndMissingTargets_AreBothNotFound(
        OrganizationMemberLifecycleOperation operation)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        Organization foreignOrganization = CreateOrganization("Foreign");
        User foreignUser = CreateUser("Foreign Target");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-2));
        await SeedAsync(foreignOrganization, foreignUser, foreignMembership);

        OrganizationMemberLifecycleMutationPersistenceResult foreign =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph, operation) with
            {
                TargetMembershipId = foreignMembership.Id
            });
        OrganizationMemberLifecycleMutationPersistenceResult missing =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph, operation) with
            {
                TargetMembershipId = Guid.Parse(
                    "2fdb787b-d41e-43da-831e-a644508d2ab2")
            });

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.NotFound,
            foreign);
        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.NotFound,
            missing);
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate, false)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate, true)]
    public async Task ExecuteAsync_AlreadyFinalState_IsIdempotentAfterAuthorization(
        OrganizationMemberLifecycleOperation operation,
        bool initiallyActive)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            initiallyActive);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph, operation));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            result);
        Assert.Equal(
            initiallyActive,
            await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ReactivateWithInactiveUser_ReturnsConflictEvenIfAlreadyActive()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            targetMembershipActive: true,
            targetUserActive: false);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationMemberLifecycleOperation.Reactivate));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult
                .InactiveUserConflict,
            result);
        Assert.True(await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Theory]
    [InlineData(ActorState.MemberRole)]
    [InlineData(ActorState.InactiveMembership)]
    [InlineData(ActorState.InactiveUser)]
    [InlineData(ActorState.InactiveOrganization)]
    public async Task ExecuteAsync_UnavailableActor_DeniesWithoutWrite(
        ActorState actorState)
    {
        TestGraph graph = await SeedGraphAsync(
            actorState == ActorState.MemberRole
                ? OrganizationRole.Member
                : OrganizationRole.Owner,
            OrganizationRole.Member,
            actorMembershipActive: actorState != ActorState.InactiveMembership,
            actorUserActive: actorState != ActorState.InactiveUser,
            organizationActive: actorState != ActorState.InactiveOrganization);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationMemberLifecycleOperation.Deactivate));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
            result);
        Assert.True(await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Theory]
    [InlineData(AssignmentShape.OpenDatedTask, true)]
    [InlineData(AssignmentShape.OpenUndatedTask, true)]
    [InlineData(AssignmentShape.CompletedTask, false)]
    [InlineData(AssignmentShape.CurrentEvent, true)]
    [InlineData(AssignmentShape.FutureEvent, true)]
    [InlineData(AssignmentShape.PastEvent, false)]
    [InlineData(AssignmentShape.CreatorOnlyTask, false)]
    [InlineData(AssignmentShape.CreatorOnlyEvent, false)]
    public async Task ExecuteAsync_Deactivate_UsesActualActiveWorkPredicates(
        AssignmentShape shape,
        bool expectedConflict)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        await SeedAssignmentAsync(graph, shape);

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationMemberLifecycleOperation.Deactivate));

        Assert.Equal(
            expectedConflict
                ? OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict
                : OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            result);
        Assert.Equal(
            expectedConflict,
            await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(expectedConflict ? 0 : 1, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_DeactivateThenReactivate_PreservesHistoricalGraph()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        var completedTask = new LegalTask(
            graph.Organization.Id,
            "Historical task",
            null,
            DateOnly.FromDateTime(Now.UtcDateTime),
            null,
            graph.TargetMembership.Id,
            graph.TargetMembership.Id,
            Now.AddDays(-2));
        completedTask.Complete(Now.AddDays(-1));
        var pastEvent = new CalendarEvent(
            graph.Organization.Id,
            "Historical event",
            null,
            Now.AddDays(-2),
            Now.AddDays(-2).AddHours(1),
            null,
            null,
            null,
            graph.TargetMembership.Id,
            graph.TargetMembership.Id,
            Now.AddDays(-3));
        var document = new LegalDocument(
            graph.Organization.Id,
            null,
            null,
            "historical.pdf",
            "0123456789abcdef0123456789abcdef",
            "application/pdf",
            1,
            new LegalDocumentContentHash(new byte[32]),
            graph.TargetMembership.Id,
            Now.AddDays(-1));
        var notification = new Notification(
            graph.Organization.Id,
            graph.TargetUser.Id,
            NotificationKind.LegalTaskDueSoon,
            null,
            completedTask.Id,
            null,
            DateOnly.FromDateTime(Now.UtcDateTime),
            null,
            Now.AddHours(-1));
        await SeedAsync(completedTask, pastEvent, document, notification);

        OrganizationMemberLifecycleMutationPersistence persistence =
            CreatePersistence();
        OrganizationMemberLifecycleMutationPersistenceResult deactivated =
            await persistence.ExecuteAsync(CreateRequest(
                graph,
                OrganizationMemberLifecycleOperation.Deactivate));
        OrganizationMemberLifecycleMutationPersistenceResult reactivated =
            await persistence.ExecuteAsync(CreateRequest(
                graph,
                OrganizationMemberLifecycleOperation.Reactivate));

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            deactivated);
        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            reactivated);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.True(await dbContext.OrganizationMemberships.AnyAsync(
            membership => membership.Id == graph.TargetMembership.Id &&
                membership.IsActive));
        Assert.True(await dbContext.LegalTasks.AnyAsync(task =>
            task.Id == completedTask.Id &&
            task.AssigneeMembershipId == graph.TargetMembership.Id &&
            task.CreatedByMembershipId == graph.TargetMembership.Id));
        Assert.True(await dbContext.CalendarEvents.AnyAsync(calendarEvent =>
            calendarEvent.Id == pastEvent.Id &&
            calendarEvent.AssigneeMembershipId == graph.TargetMembership.Id &&
            calendarEvent.CreatedByMembershipId == graph.TargetMembership.Id));
        Assert.True(await dbContext.LegalDocuments.AnyAsync(candidate =>
            candidate.Id == document.Id &&
            candidate.UploadedByMembershipId == graph.TargetMembership.Id));
        Assert.True(await dbContext.Notifications.AnyAsync(candidate =>
            candidate.Id == notification.Id &&
            candidate.RecipientUserId == graph.TargetUser.Id));
        Assert.Equal(2, await dbContext.AuditLogs.CountAsync());
        Assert.Equal(
            [
                AuditEventType.OrganizationMembershipDeactivated,
                AuditEventType.OrganizationMembershipReactivated
            ],
            await dbContext.AuditLogs
                .AsNoTracking()
                .OrderBy(auditLog => auditLog.OccurredAt)
                .ThenBy(auditLog => auditLog.EventType)
                .Select(auditLog => auditLog.EventType)
                .ToArrayAsync());
    }

    private OrganizationMemberLifecycleMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new OrganizationMemberLifecycleMutationPersistence(
            options,
            new FixedTimeProvider(Now));
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole,
        OrganizationRole targetRole,
        bool targetMembershipActive = true,
        bool targetUserActive = true,
        bool actorMembershipActive = true,
        bool actorUserActive = true,
        bool organizationActive = true)
    {
        Organization organization = CreateOrganization("Lifecycle");
        User actorUser = CreateUser("Lifecycle Actor");
        User targetUser = CreateUser("Lifecycle Target");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            Now.AddHours(-2));
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            targetRole,
            Now.AddHours(-2));

        if (!organizationActive)
        {
            organization.Deactivate();
        }

        if (!actorUserActive)
        {
            actorUser.Deactivate();
        }

        if (!actorMembershipActive)
        {
            actorMembership.Deactivate();
        }

        if (!targetUserActive)
        {
            targetUser.Deactivate();
        }

        if (!targetMembershipActive)
        {
            targetMembership.Deactivate();
        }

        await SeedAsync(
            organization,
            actorUser,
            targetUser,
            actorMembership,
            targetMembership);

        return new TestGraph(
            organization,
            actorUser,
            targetUser,
            actorMembership,
            targetMembership);
    }

    private async Task SeedAssignmentAsync(
        TestGraph graph,
        AssignmentShape shape)
    {
        object assignment = shape switch
        {
            AssignmentShape.OpenDatedTask => CreateTask(
                graph,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                DateOnly.FromDateTime(Now.UtcDateTime)),
            AssignmentShape.OpenUndatedTask => CreateTask(
                graph,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                null),
            AssignmentShape.CompletedTask => CreateCompletedTask(graph),
            AssignmentShape.CurrentEvent => CreateEvent(
                graph,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                Now.AddHours(-1),
                Now.AddHours(1)),
            AssignmentShape.FutureEvent => CreateEvent(
                graph,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                Now.AddHours(1),
                Now.AddHours(2)),
            AssignmentShape.PastEvent => CreateEvent(
                graph,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                Now.AddHours(-2),
                Now.AddHours(-1)),
            AssignmentShape.CreatorOnlyTask => CreateTask(
                graph,
                null,
                graph.TargetMembership.Id,
                null),
            AssignmentShape.CreatorOnlyEvent => CreateEvent(
                graph,
                null,
                graph.TargetMembership.Id,
                Now.AddHours(1),
                Now.AddHours(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        await SeedAsync(assignment);
    }

    private static LegalTask CreateTask(
        TestGraph graph,
        Guid? assigneeMembershipId,
        Guid creatorMembershipId,
        DateOnly? dueDate)
    {
        return new LegalTask(
            graph.Organization.Id,
            "Lifecycle task",
            null,
            dueDate,
            null,
            assigneeMembershipId,
            creatorMembershipId,
            Now.AddDays(-1));
    }

    private static LegalTask CreateCompletedTask(TestGraph graph)
    {
        LegalTask task = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            null);
        task.Complete(Now.AddHours(-1));
        return task;
    }

    private static CalendarEvent CreateEvent(
        TestGraph graph,
        Guid? assigneeMembershipId,
        Guid creatorMembershipId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        return new CalendarEvent(
            graph.Organization.Id,
            "Lifecycle event",
            null,
            startsAt,
            endsAt,
            null,
            null,
            null,
            assigneeMembershipId,
            creatorMembershipId,
            Now.AddDays(-1));
    }

    private static OrganizationMemberLifecycleMutationPersistenceRequest
        CreateRequest(
            TestGraph graph,
            OrganizationMemberLifecycleOperation operation)
    {
        return new OrganizationMemberLifecycleMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            operation);
    }

    private async Task<bool> FindMembershipActivityAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.IsActive)
            .SingleAsync();
    }

    private async Task<int> CountAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.CountAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant()}-{Guid.NewGuid():N}",
            Now.AddDays(-3));
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            Now.AddDays(-3));
    }

    public enum ActorState
    {
        MemberRole,
        InactiveMembership,
        InactiveUser,
        InactiveOrganization
    }

    public enum AssignmentShape
    {
        OpenDatedTask,
        OpenUndatedTask,
        CompletedTask,
        CurrentEvent,
        FutureEvent,
        PastEvent,
        CreatorOnlyTask,
        CreatorOnlyEvent
    }

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        User TargetUser,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
