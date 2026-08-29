using System.Data;
using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Update;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Reopen;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberLifecycleMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        26,
        16,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(AssignmentKind.LegalTask)]
    [InlineData(AssignmentKind.CalendarEvent)]
    public async Task AssignmentWinsBeforeLifecycleLock_DeactivationReturnsConflict(
        AssignmentKind kind)
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<object>? assignment = null;
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation =
            null;

        try
        {
            assignment = StartAssignmentAsync(graph, kind, pause, timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            deactivation = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedLockAsync(
                kind == AssignmentKind.CalendarEvent
                    ? "organizations"
                    : "organization_memberships",
                timeout.Token);

            pause.Release();
            object assignmentResult = await assignment.WaitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult lifecycleResult =
                await deactivation.WaitAsync(timeout.Token);

            AssertAssignmentPersisted(kind, assignmentResult);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                lifecycleResult);
            Assert.True(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(assignment);
            await DrainTaskAsync(deactivation);
        }
    }

    [Theory]
    [InlineData(AssignmentKind.LegalTask)]
    [InlineData(AssignmentKind.CalendarEvent)]
    public async Task DeactivationWinsBeforeAssignment_AssignmentRejectsInactiveMember(
        AssignmentKind kind)
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation =
            null;
        Task<object>? assignment = null;

        try
        {
            deactivation = CreateLifecyclePersistence(pause).ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            assignment = StartAssignmentAsync(
                graph,
                kind,
                interceptor: null,
                timeout.Token);
            await WaitForBlockedLockAsync(
                "organization_memberships",
                timeout.Token);

            pause.Release();
            OrganizationMemberLifecycleMutationPersistenceResult lifecycleResult =
                await deactivation.WaitAsync(timeout.Token);
            object assignmentResult = await assignment.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                lifecycleResult);
            AssertAssignmentUnavailable(kind, assignmentResult);
            Assert.False(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(deactivation);
            await DrainTaskAsync(assignment);
        }
    }

    [Theory]
    [InlineData(OperationalTransitionKind.ReopenTask)]
    [InlineData(OperationalTransitionKind.RescheduleEvent)]
    public async Task OperationalTransitionWins_DeactivationObservesActiveWork(
        OperationalTransitionKind kind)
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        Guid workId = await SeedHistoricalAssignedWorkAsync(graph, kind);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<object>? transition = null;
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation =
            null;

        try
        {
            transition = StartOperationalTransitionAsync(
                graph,
                workId,
                kind,
                pause,
                timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            deactivation = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedLockAsync(
                kind == OperationalTransitionKind.RescheduleEvent
                    ? "organizations"
                    : "organization_memberships",
                timeout.Token);

            pause.Release();
            object transitionResult = await transition.WaitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult lifecycleResult =
                await deactivation.WaitAsync(timeout.Token);

            AssertOperationalTransitionSucceeded(kind, transitionResult);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                lifecycleResult);
            Assert.True(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(transition);
            await DrainTaskAsync(deactivation);
        }
    }

    [Theory]
    [InlineData(OperationalTransitionKind.ReopenTask)]
    [InlineData(OperationalTransitionKind.RescheduleEvent)]
    public async Task DeactivationWins_OperationalTransitionRejectsInactiveAssignee(
        OperationalTransitionKind kind)
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        Guid workId = await SeedHistoricalAssignedWorkAsync(graph, kind);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation =
            null;
        Task<object>? transition = null;

        try
        {
            deactivation = CreateLifecyclePersistence(pause).ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            transition = StartOperationalTransitionAsync(
                graph,
                workId,
                kind,
                interceptor: null,
                timeout.Token);
            await WaitForBlockedLockAsync(
                "organization_memberships",
                timeout.Token);

            pause.Release();
            OrganizationMemberLifecycleMutationPersistenceResult lifecycleResult =
                await deactivation.WaitAsync(timeout.Token);
            object transitionResult = await transition.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                lifecycleResult);
            AssertOperationalTransitionUnavailable(kind, transitionResult);
            Assert.False(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(deactivation);
            await DrainTaskAsync(transition);
        }
    }

    [Fact]
    public async Task ConcurrentDeactivations_SerializeAndBothSucceed()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        OrganizationMemberLifecycleMutationPersistence persistence =
            CreateLifecyclePersistence();
        OrganizationMemberLifecycleMutationPersistenceRequest request =
            CreateLifecycleRequest(
                graph,
                OrganizationMemberLifecycleOperation.Deactivate);

        OrganizationMemberLifecycleMutationPersistenceResult[] results =
            await Task.WhenAll(
                    persistence.ExecuteAsync(request, timeout.Token),
                    persistence.ExecuteAsync(request, timeout.Token))
                .WaitAsync(timeout.Token);

        Assert.All(
            results,
            result => Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                result));
        Assert.False(await FindMembershipActivityAsync(graph.TargetMembership.Id));
        Assert.Equal(1, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task DeactivateThenWaitingReactivate_SerializesToActiveFinalState()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation =
            null;
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? reactivation =
            null;

        try
        {
            deactivation = CreateLifecyclePersistence(pause).ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            reactivation = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Reactivate),
                timeout.Token);
            await WaitForBlockedLockAsync("organizations", timeout.Token);

            pause.Release();
            OrganizationMemberLifecycleMutationPersistenceResult[] results =
                await Task.WhenAll(deactivation, reactivation)
                    .WaitAsync(timeout.Token);

            Assert.All(
                results,
                result => Assert.Equal(
                    OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                    result));
            Assert.True(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
            Assert.Equal(2, await CountAuditLogsAsync());
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(deactivation);
            await DrainTaskAsync(reactivation);
        }
    }

    [Fact]
    public async Task ActorDemotedWhileLifecycleWaits_IsDeniedFromLockedState()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Administrator);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        OrganizationMembership actor = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            timeout.Token);
        actor.ChangeRole(OrganizationRole.Member);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? lifecycle =
            null;

        try
        {
            lifecycle = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedLockAsync(
                "organization_memberships",
                timeout.Token);
            await LockOrganizationAsync(
                blockerContext,
                graph.Organization.Id,
                timeout.Token);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult result =
                await lifecycle.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
                result);
            Assert.True(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
            Assert.Equal(0, await CountAuditLogsAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(lifecycle);
        }
    }

    [Fact]
    public async Task TargetPromotedWhileAdministratorWaits_IsDeniedFromLockedState()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Administrator);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        OrganizationMembership target = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        target.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? lifecycle =
            null;

        try
        {
            lifecycle = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedLockAsync(
                "organization_memberships",
                timeout.Token);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult result =
                await lifecycle.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
                result);
            Assert.True(await FindMembershipActivityAsync(
                graph.TargetMembership.Id));
            Assert.Equal(0, await CountAuditLogsAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(lifecycle);
        }
    }

    [Fact]
    public async Task TeamCRoleMutationAndLifecycle_UseCompatibleOrderWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        var pause = new PausingSaveChangesInterceptor();
        Task<OrganizationMemberRoleMutationPersistenceResult>? roleMutation = null;
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? lifecycle =
            null;

        try
        {
            roleMutation = CreateRolePersistence(pause).ExecuteAsync(
                new OrganizationMemberRoleMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    graph.TargetMembership.Id,
                    OrganizationRole.Administrator,
                    OrganizationRole.Member),
                timeout.Token);
            await pause.Entered.WaitAsync(timeout.Token);

            lifecycle = CreateLifecyclePersistence().ExecuteAsync(
                CreateLifecycleRequest(
                    graph,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedLockAsync("organizations", timeout.Token);

            pause.Release();
            OrganizationMemberRoleMutationPersistenceResult roleResult =
                await roleMutation.WaitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult lifecycleResult =
                await lifecycle.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.Succeeded,
                roleResult);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                lifecycleResult);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            OrganizationMembership target = await dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == graph.TargetMembership.Id);
            Assert.Equal(OrganizationRole.Administrator, target.Role);
            Assert.False(target.IsActive);
            Assert.Equal(2, await dbContext.AuditLogs.CountAsync());
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(roleMutation);
            await DrainTaskAsync(lifecycle);
        }
    }

    private Task<object> StartAssignmentAsync(
        TestGraph graph,
        AssignmentKind kind,
        IInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            AssignmentKind.LegalTask => CreateLegalTaskAssignmentAsync(
                graph,
                interceptor,
                cancellationToken),
            AssignmentKind.CalendarEvent => CreateCalendarEventAssignmentAsync(
                graph,
                interceptor,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private async Task<object> StartOperationalTransitionAsync(
        TestGraph graph,
        Guid workId,
        OperationalTransitionKind kind,
        IInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var organizationAccess = new OrganizationAccessAuthorization(
            new OrganizationAccessLookup(queryContext));

        if (kind == OperationalTransitionKind.ReopenTask)
        {
            var useCase = new ReopenLegalTaskUseCase(
                organizationAccess,
                new LegalTaskMutationAuthorization(),
                new LegalTaskMutationPersistence(
                    CreateOptions(interceptor),
                    new FixedTimeProvider(Now)));
            return await useCase.ExecuteAsync(
                new ReopenLegalTaskCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    workId),
                cancellationToken);
        }

        var calendarAccess = new CalendarEventAccessAuthorization(
            organizationAccess);
        var updateUseCase = new UpdateCalendarEventUseCase(
            calendarAccess,
            new CalendarEventActionAuthorization(),
            new CalendarEventMutationPersistence(
                CreateOptions(interceptor),
                new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));
        return await updateUseCase.ExecuteAsync(
            new UpdateCalendarEventCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                workId,
                "Rescheduled assignment",
                null,
                Now.AddHours(1),
                Now.AddHours(2),
                null,
                null,
                null),
            cancellationToken);
    }

    private async Task<object> CreateLegalTaskAssignmentAsync(
        TestGraph graph,
        IInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        var persistence = new LegalTaskCreationPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(Now));
        var request = new LegalTaskCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            null);

        return await persistence.ExecuteAsync(
            request,
            state =>
            {
                if (state.Actor?.IsMembershipActive != true ||
                    state.Actor.IsUserActive != true)
                {
                    return LegalTaskCreationDecision.AccessDenied;
                }

                if (state.Assignee?.IsMembershipActive != true ||
                    state.Assignee.IsUserActive != true)
                {
                    return LegalTaskCreationDecision.RelatedAssigneeUnavailable;
                }

                return LegalTaskCreationDecision.Persist(new LegalTask(
                    graph.Organization.Id,
                    "Concurrent assignment",
                    null,
                    null,
                    null,
                    graph.TargetMembership.Id,
                    graph.ActorMembership.Id,
                    Now));
            },
            cancellationToken);
    }

    private async Task<object> CreateCalendarEventAssignmentAsync(
        TestGraph graph,
        IInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        var persistence = new CalendarEventCreationPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(Now));
        var request = new CalendarEventCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            null,
            null,
            graph.TargetMembership.Id);

        return await persistence.ExecuteAsync(
            request,
            state =>
            {
                if (!state.IsOrganizationActive ||
                    state.Actor?.IsMembershipActive != true ||
                    state.Actor.IsUserActive != true)
                {
                    return CalendarEventCreationDecision.AccessDenied;
                }

                if (state.Assignee?.IsMembershipActive != true ||
                    state.Assignee.IsUserActive != true)
                {
                    return CalendarEventCreationDecision
                        .RelatedAssigneeUnavailable;
                }

                return CalendarEventCreationDecision.Persist(new CalendarEvent(
                    graph.Organization.Id,
                    "Concurrent assignment",
                    null,
                    Now.AddHours(1),
                    Now.AddHours(2),
                    null,
                    null,
                    null,
                    graph.TargetMembership.Id,
                    graph.ActorMembership.Id,
                    Now));
            },
            cancellationToken);
    }

    private OrganizationMemberLifecycleMutationPersistence
        CreateLifecyclePersistence(IInterceptor? interceptor = null)
    {
        return new OrganizationMemberLifecycleMutationPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(Now));
    }

    private OrganizationMemberRoleMutationPersistence CreateRolePersistence(
        IInterceptor? interceptor = null)
    {
        return new OrganizationMemberRoleMutationPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(Now));
    }

    private DbContextOptions<EnmaDbContext> CreateOptions(
        IInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<EnmaDbContext> builder =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private async Task<TestGraph> SeedGraphAsync(OrganizationRole actorRole)
    {
        var organization = new Organization(
            "Lifecycle concurrency",
            $"lifecycle-concurrency-{Guid.NewGuid():N}",
            Now.AddDays(-1));
        var actorUser = new User(
            "Lifecycle actor",
            $"lifecycle-actor+{Guid.NewGuid():N}@example.test",
            Now.AddDays(-1));
        var targetUser = new User(
            "Lifecycle target",
            $"lifecycle-target+{Guid.NewGuid():N}@example.test",
            Now.AddDays(-1));
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            Now.AddDays(-1));
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Member,
            Now.AddDays(-1));
        await SeedAsync(
            organization,
            actorUser,
            targetUser,
            actorMembership,
            targetMembership);

        return new TestGraph(
            organization,
            actorUser,
            actorMembership,
            targetMembership);
    }

    private async Task<Guid> SeedHistoricalAssignedWorkAsync(
        TestGraph graph,
        OperationalTransitionKind kind)
    {
        if (kind == OperationalTransitionKind.ReopenTask)
        {
            var legalTask = new LegalTask(
                graph.Organization.Id,
                "Completed assignment",
                null,
                null,
                null,
                graph.TargetMembership.Id,
                graph.ActorMembership.Id,
                Now.AddDays(-2));
            legalTask.Complete(Now.AddDays(-1));
            await SeedAsync(legalTask);
            return legalTask.Id;
        }

        var calendarEvent = new CalendarEvent(
            graph.Organization.Id,
            "Past assignment",
            null,
            Now.AddDays(-2),
            Now.AddDays(-2).AddHours(1),
            null,
            null,
            null,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            Now.AddDays(-3));
        await SeedAsync(calendarEvent);
        return calendarEvent.Id;
    }

    private static OrganizationMemberLifecycleMutationPersistenceRequest
        CreateLifecycleRequest(
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

    private async Task WaitForBlockedLockAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();
        string tablePattern = $"%FROM {tableName}%";

        while (true)
        {
            int count = await observationContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE {tablePattern}
                  AND query ILIKE '%FOR UPDATE%'
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task<OrganizationMembership> LockMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {membershipId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .Single();
    }

    private static async Task<Organization> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.Organizations
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organizations
                    WHERE id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .Single();
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

    private static void AssertAssignmentPersisted(
        AssignmentKind kind,
        object result)
    {
        if (kind == AssignmentKind.LegalTask)
        {
            Assert.Equal(
                LegalTaskCreationDecisionStatus.Persist,
                Assert.IsType<LegalTaskCreationPersistenceResult>(result).Status);
            return;
        }

        Assert.Equal(
            CalendarEventCreationDecisionStatus.Persist,
            Assert.IsType<CalendarEventCreationPersistenceResult>(result).Status);
    }

    private static void AssertAssignmentUnavailable(
        AssignmentKind kind,
        object result)
    {
        if (kind == AssignmentKind.LegalTask)
        {
            Assert.Equal(
                LegalTaskCreationDecisionStatus.RelatedAssigneeUnavailable,
                Assert.IsType<LegalTaskCreationPersistenceResult>(result).Status);
            return;
        }

        Assert.Equal(
            CalendarEventCreationDecisionStatus.RelatedAssigneeUnavailable,
            Assert.IsType<CalendarEventCreationPersistenceResult>(result).Status);
    }

    private static void AssertOperationalTransitionSucceeded(
        OperationalTransitionKind kind,
        object result)
    {
        if (kind == OperationalTransitionKind.ReopenTask)
        {
            Assert.Equal(
                ReopenLegalTaskResult.Succeeded,
                Assert.IsType<ReopenLegalTaskResult>(result));
            return;
        }

        Assert.Equal(
            UpdateCalendarEventResult.Succeeded,
            Assert.IsType<UpdateCalendarEventResult>(result));
    }

    private static void AssertOperationalTransitionUnavailable(
        OperationalTransitionKind kind,
        object result)
    {
        if (kind == OperationalTransitionKind.ReopenTask)
        {
            Assert.Equal(
                ReopenLegalTaskResult.RelatedAssigneeUnavailable,
                Assert.IsType<ReopenLegalTaskResult>(result));
            return;
        }

        Assert.Equal(
            UpdateCalendarEventResult.RelatedAssigneeUnavailable,
            Assert.IsType<UpdateCalendarEventResult>(result));
    }

    private static CancellationTokenSource CreateTimeout()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    private static async Task RollbackIfActiveAsync(
        IDbContextTransaction transaction)
    {
        if (transaction.GetDbTransaction().Connection is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public enum AssignmentKind
    {
        LegalTask,
        CalendarEvent
    }

    public enum OperationalTransitionKind
    {
        ReopenTask,
        RescheduleEvent
    }

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class PausingSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release()
        {
            _release.TrySetResult();
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
