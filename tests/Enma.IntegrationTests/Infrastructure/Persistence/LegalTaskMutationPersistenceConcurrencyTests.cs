using System.Data;
using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeededAt = new(
        2026,
        8,
        14,
        20,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset TaskCreatedAt = SeededAt.AddHours(1);
    private static readonly DateTimeOffset FirstCompletedAt = SeededAt.AddHours(2);
    private static readonly DateTimeOffset SecondCompletedAt = SeededAt.AddHours(3);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpdateAsync_WhenUpdateLocksFirst_SerializesAndLastValidUpdateWins()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask task = CreateTask(graph, "Original");
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        LegalTask firstUpdate = await LockTaskAsync(
            blockerContext,
            task.Id,
            graph.Organization.Id,
            timeout.Token);
        firstUpdate.ChangeDetails("First update", null, null, null);
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(queryContext);
        Task<UpdateLegalTaskResult>? secondUpdate = null;

        try
        {
            secondUpdate = UpdateTitleAsync(
                useCase,
                graph,
                task.Id,
                "Second update",
                timeout.Token);
            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            Assert.False(secondUpdate.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            UpdateLegalTaskResult result = await secondUpdate.WaitAsync(timeout.Token);

            Assert.Equal(UpdateLegalTaskResult.Succeeded, result);
            Assert.Equal("Second update", (await FindTaskAsync(task.Id)).Title);
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(secondUpdate);
        }
    }

    [Fact]
    public async Task UpdateAndComplete_BothLockOrders_ProduceSerializedOutcomes()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask updateFirstTask = CreateTask(graph, "Update first original");
        LegalTask completeFirstTask = CreateTask(graph, "Complete first original");
        await SeedAsync(updateFirstTask, completeFirstTask);
        using var timeout = CreateTimeout();

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                updateFirstTask.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.ChangeDetails("Updated before completion", null, null, null);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<CompleteLegalTaskResult> completion = CreateCompleteUseCase(queryContext)
                .ExecuteAsync(
                    new CompleteLegalTaskCommand(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        updateFirstTask.Id),
                    timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await blockerTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                CompleteLegalTaskResult.Succeeded,
                await completion.WaitAsync(timeout.Token));
        }

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                completeFirstTask.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.Complete(FirstCompletedAt);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<UpdateLegalTaskResult> update = UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                completeFirstTask.Id,
                "Forbidden after completion",
                timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await blockerTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                UpdateLegalTaskResult.Conflict,
                await update.WaitAsync(timeout.Token));
        }

        LegalTask updateFirst = await FindTaskAsync(updateFirstTask.Id);
        LegalTask completeFirst = await FindTaskAsync(completeFirstTask.Id);
        Assert.Equal("Updated before completion", updateFirst.Title);
        Assert.Equal(SecondCompletedAt, updateFirst.CompletedAt);
        Assert.Equal("Complete first original", completeFirst.Title);
        Assert.Equal(FirstCompletedAt, completeFirst.CompletedAt);
    }

    [Fact]
    public async Task ReassignAsync_WhenReassignLocksFirst_SerializesAndLastAcceptedTargetWins()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask task = CreateTask(graph, "Reassign race");
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        LegalTask locked = await LockTaskAsync(
            blockerContext,
            task.Id,
            graph.Organization.Id,
            timeout.Token);
        locked.ChangeAssignee(graph.FirstTargetMembership.Id);
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<ChangeLegalTaskAssigneeResult>? second = null;

        try
        {
            second = ChangeAssigneeAsync(
                CreateAssignmentUseCase(queryContext),
                graph,
                task.Id,
                graph.SecondTargetMembership.Id,
                timeout.Token);
            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await blockerTransaction.CommitAsync(timeout.Token);

            Assert.Equal(
                ChangeLegalTaskAssigneeResult.Succeeded,
                await second.WaitAsync(timeout.Token));
            Assert.Equal(
                graph.SecondTargetMembership.Id,
                (await FindTaskAsync(task.Id)).AssigneeMembershipId);
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(second);
        }
    }

    [Fact]
    public async Task ReassignAndComplete_BothLockOrders_ProduceSerializedOutcomes()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask reassignFirstTask = CreateTask(graph, "Reassign first");
        LegalTask completeFirstTask = CreateTask(graph, "Complete first");
        await SeedAsync(reassignFirstTask, completeFirstTask);
        using var timeout = CreateTimeout();

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                reassignFirstTask.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.ChangeAssignee(graph.FirstTargetMembership.Id);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<CompleteLegalTaskResult> complete = CreateCompleteUseCase(queryContext)
                .ExecuteAsync(new CompleteLegalTaskCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    reassignFirstTask.Id), timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(
                CompleteLegalTaskResult.Succeeded,
                await complete.WaitAsync(timeout.Token));
        }

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                completeFirstTask.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.Complete(FirstCompletedAt);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<ChangeLegalTaskAssigneeResult> reassign = ChangeAssigneeAsync(
                CreateAssignmentUseCase(queryContext),
                graph,
                completeFirstTask.Id,
                graph.FirstTargetMembership.Id,
                timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(
                ChangeLegalTaskAssigneeResult.Conflict,
                await reassign.WaitAsync(timeout.Token));
        }

        LegalTask reassignFirst = await FindTaskAsync(reassignFirstTask.Id);
        LegalTask completeFirst = await FindTaskAsync(completeFirstTask.Id);
        Assert.Equal(graph.FirstTargetMembership.Id, reassignFirst.AssigneeMembershipId);
        Assert.Equal(SecondCompletedAt, reassignFirst.CompletedAt);
        Assert.Null(completeFirst.AssigneeMembershipId);
        Assert.Equal(FirstCompletedAt, completeFirst.CompletedAt);
    }

    [Fact]
    public async Task CompleteAndReopen_ConcurrentOrders_PreserveIdempotencyAndSerializedState()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask doubleComplete = CreateTask(graph, "Double complete");
        LegalTask completeThenReopen = CreateTask(graph, "Complete then reopen");
        LegalTask reopenThenComplete = CreateTask(
            graph,
            "Reopen then complete",
            completed: true);
        await SeedAsync(doubleComplete, completeThenReopen, reopenThenComplete);
        using var timeout = CreateTimeout();

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                doubleComplete.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.Complete(FirstCompletedAt);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<CompleteLegalTaskResult> second = CreateCompleteUseCase(queryContext)
                .ExecuteAsync(new CompleteLegalTaskCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    doubleComplete.Id), timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(
                CompleteLegalTaskResult.Succeeded,
                await second.WaitAsync(timeout.Token));
        }

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                completeThenReopen.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.Complete(FirstCompletedAt);
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<ReopenLegalTaskResult> reopen = CreateReopenUseCase(queryContext)
                .ExecuteAsync(new ReopenLegalTaskCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    completeThenReopen.Id), timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(
                ReopenLegalTaskResult.Succeeded,
                await reopen.WaitAsync(timeout.Token));
        }

        await using (EnmaDbContext blockerContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token))
        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            LegalTask locked = await LockTaskAsync(
                blockerContext,
                reopenThenComplete.Id,
                graph.Organization.Id,
                timeout.Token);
            locked.Reopen();
            await blockerContext.SaveChangesAsync(timeout.Token);
            Task<CompleteLegalTaskResult> complete = CreateCompleteUseCase(queryContext)
                .ExecuteAsync(new CompleteLegalTaskCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    reopenThenComplete.Id), timeout.Token);

            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(
                CompleteLegalTaskResult.Succeeded,
                await complete.WaitAsync(timeout.Token));
        }

        Assert.Equal(FirstCompletedAt, (await FindTaskAsync(doubleComplete.Id)).CompletedAt);
        Assert.Null((await FindTaskAsync(completeThenReopen.Id)).CompletedAt);
        Assert.Equal(SecondCompletedAt, (await FindTaskAsync(reopenThenComplete.Id)).CompletedAt);

        await using EnmaDbContext auditContext = fixture.CreateDbContext();
        AuditLog[] auditLogs = await auditContext.AuditLogs
            .AsNoTracking()
            .ToArrayAsync();
        Assert.Equal(2, auditLogs.Length);
        Assert.DoesNotContain(
            auditLogs,
            auditLog => auditLog.EntityId == doubleComplete.Id);
        Assert.Contains(
            auditLogs,
            auditLog =>
                auditLog.EntityId == completeThenReopen.Id &&
                auditLog.EventType == AuditEventType.LegalTaskReopened);
        Assert.Contains(
            auditLogs,
            auditLog =>
                auditLog.EntityId == reopenThenComplete.Id &&
                auditLog.EventType == AuditEventType.LegalTaskCompleted);
    }

    [Fact]
    public async Task ReopenWithUpdateAndReassign_BothSerializedStatesAreObserved()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask reopenThenUpdate = CreateTask(graph, "Reopen update", true);
        LegalTask updateThenReopen = CreateTask(graph, "Update conflict", true);
        LegalTask reopenThenReassign = CreateTask(graph, "Reopen reassign", true);
        LegalTask reassignThenReopen = CreateTask(graph, "Reassign conflict", true);
        await SeedAsync(
            reopenThenUpdate,
            updateThenReopen,
            reopenThenReassign,
            reassignThenReopen);
        using var timeout = CreateTimeout();

        await ReopenFirstThenRunAsync(
            graph,
            reopenThenUpdate.Id,
            queryContext => UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                reopenThenUpdate.Id,
                "Updated after reopen",
                timeout.Token),
            UpdateLegalTaskResult.Succeeded,
            timeout.Token);

        await HoldCompletedThenRunAsync(
            graph,
            updateThenReopen.Id,
            queryContext => UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                updateThenReopen.Id,
                "Must conflict",
                timeout.Token),
            UpdateLegalTaskResult.Conflict,
            timeout.Token);
        await using (EnmaDbContext reopenContext = fixture.CreateDbContext())
        {
            Assert.Equal(
                ReopenLegalTaskResult.Succeeded,
                await CreateReopenUseCase(reopenContext).ExecuteAsync(
                    new ReopenLegalTaskCommand(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        updateThenReopen.Id),
                    timeout.Token));
        }

        await ReopenFirstThenRunAsync(
            graph,
            reopenThenReassign.Id,
            queryContext => ChangeAssigneeAsync(
                CreateAssignmentUseCase(queryContext),
                graph,
                reopenThenReassign.Id,
                graph.FirstTargetMembership.Id,
                timeout.Token),
            ChangeLegalTaskAssigneeResult.Succeeded,
            timeout.Token);

        await HoldCompletedThenRunAsync(
            graph,
            reassignThenReopen.Id,
            queryContext => ChangeAssigneeAsync(
                CreateAssignmentUseCase(queryContext),
                graph,
                reassignThenReopen.Id,
                graph.FirstTargetMembership.Id,
                timeout.Token),
            ChangeLegalTaskAssigneeResult.Conflict,
            timeout.Token);
        await using (EnmaDbContext reopenContext = fixture.CreateDbContext())
        {
            Assert.Equal(
                ReopenLegalTaskResult.Succeeded,
                await CreateReopenUseCase(reopenContext).ExecuteAsync(
                    new ReopenLegalTaskCommand(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        reassignThenReopen.Id),
                    timeout.Token));
        }

        Assert.Equal("Updated after reopen", (await FindTaskAsync(reopenThenUpdate.Id)).Title);
        Assert.Equal("Update conflict", (await FindTaskAsync(updateThenReopen.Id)).Title);
        Assert.Null((await FindTaskAsync(updateThenReopen.Id)).CompletedAt);
        Assert.Equal(
            graph.FirstTargetMembership.Id,
            (await FindTaskAsync(reopenThenReassign.Id)).AssigneeMembershipId);
        Assert.Null((await FindTaskAsync(reassignThenReopen.Id)).AssigneeMembershipId);
        Assert.Null((await FindTaskAsync(reassignThenReopen.Id)).CompletedAt);
    }

    [Theory]
    [InlineData(ActorChange.RoleDemotion)]
    [InlineData(ActorChange.MembershipDeactivation)]
    [InlineData(ActorChange.UserDeactivation)]
    public async Task UpdateAsync_WhenActorChangeCommitsFirst_RevalidatesAndDenies(
        ActorChange actorChange)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Administrator);
        LegalTask task = CreateTask(
            graph,
            "Actor change",
            assigneeMembershipId: graph.FirstTargetMembership.Id,
            createdByMembershipId: graph.FirstTargetMembership.Id);
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        if (actorChange == ActorChange.UserDeactivation)
        {
            User actorUser = await blockerContext.Users.SingleAsync(
                user => user.Id == graph.ActorUser.Id,
                timeout.Token);
            actorUser.Deactivate();
        }
        else
        {
            OrganizationMembership actor = await blockerContext
                .OrganizationMemberships.SingleAsync(
                    membership => membership.Id == graph.ActorMembership.Id,
                    timeout.Token);

            if (actorChange == ActorChange.RoleDemotion)
            {
                actor.ChangeRole(OrganizationRole.Member);
            }
            else
            {
                actor.Deactivate();
            }
        }

        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<UpdateLegalTaskResult>? update = null;

        try
        {
            update = UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                task.Id,
                "Must not commit",
                timeout.Token);
            await WaitForBlockedSelectAsync(
                actorChange == ActorChange.UserDeactivation
                    ? "users"
                    : "organization_memberships",
                timeout.Token);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(
                UpdateLegalTaskResult.AccessDenied,
                await update.WaitAsync(timeout.Token));
            Assert.Equal("Actor change", (await FindTaskAsync(task.Id)).Title);
        }
        finally
        {
            await RollbackIfActiveAsync(transaction);
            await DrainTaskAsync(update);
        }
    }

    [Theory]
    [InlineData(ActorChange.RoleDemotion)]
    [InlineData(ActorChange.MembershipDeactivation)]
    [InlineData(ActorChange.UserDeactivation)]
    public async Task ExecuteAsync_WhenMutationLocksActorFirst_ActorChangeWaits(
        ActorChange actorChange)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Administrator);
        LegalTask task = CreateTask(graph, "Mutation wins");
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        using var releaseDecision = new ManualResetEventSlim(false);
        var locksAcquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LegalTaskMutationPersistence persistence = CreatePersistence();
        var request = CreatePersistenceRequest(graph, task.Id);
        Task<LegalTaskMutationPersistenceResult>? mutation = null;
        Task? actorMutation = null;

        try
        {
            mutation = Task.Run(
                () => persistence.ExecuteAsync(
                    request,
                    static _ => null,
                    state =>
                    {
                        Assert.Equal(
                            OrganizationRole.Administrator,
                            state.Actor?.Role);
                        locksAcquired.TrySetResult(true);
                        releaseDecision.Wait(timeout.Token);
                        state.LegalTask.ChangeDetails(
                            "Mutation committed",
                            null,
                            null,
                            null);
                        return LegalTaskMutationDecision.Persist;
                    },
                    timeout.Token),
                timeout.Token);
            await locksAcquired.Task.WaitAsync(timeout.Token);
            actorMutation = ChangeActorAsync(
                graph.ActorMembership.Id,
                graph.ActorUser.Id,
                actorChange,
                timeout.Token);
            await WaitForBlockedUpdateAsync(
                actorChange == ActorChange.UserDeactivation
                    ? "users"
                    : "organization_memberships",
                timeout.Token);
            Assert.False(actorMutation.IsCompleted);

            releaseDecision.Set();
            Assert.Equal(
                LegalTaskMutationPersistenceResult.Succeeded,
                await mutation.WaitAsync(timeout.Token));
            await actorMutation.WaitAsync(timeout.Token);

            Assert.Equal("Mutation committed", (await FindTaskAsync(task.Id)).Title);
        }
        finally
        {
            releaseDecision.Set();
            await DrainTaskAsync(mutation);
            await DrainTaskAsync(actorMutation);
        }
    }

    [Theory]
    [InlineData(TargetChange.MembershipDeactivation)]
    [InlineData(TargetChange.UserDeactivation)]
    public async Task ChangeAssigneeAsync_WhenTargetChangeCommitsFirst_ReturnsUnavailable(
        TargetChange targetChange)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask task = CreateTask(graph, "Target loses eligibility");
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);

        if (targetChange == TargetChange.MembershipDeactivation)
        {
            OrganizationMembership target = await blockerContext
                .OrganizationMemberships.SingleAsync(
                    membership => membership.Id == graph.FirstTargetMembership.Id,
                    timeout.Token);
            target.Deactivate();
        }
        else
        {
            User target = await blockerContext.Users.SingleAsync(
                user => user.Id == graph.FirstTargetUser.Id,
                timeout.Token);
            target.Deactivate();
        }

        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<ChangeLegalTaskAssigneeResult>? reassignment = null;

        try
        {
            reassignment = ChangeAssigneeAsync(
                CreateAssignmentUseCase(queryContext),
                graph,
                task.Id,
                graph.FirstTargetMembership.Id,
                timeout.Token);
            await WaitForBlockedSelectAsync(
                targetChange == TargetChange.MembershipDeactivation
                    ? "organization_memberships"
                    : "users",
                timeout.Token);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(
                ChangeLegalTaskAssigneeResult.RelatedAssigneeUnavailable,
                await reassignment.WaitAsync(timeout.Token));
            Assert.Null((await FindTaskAsync(task.Id)).AssigneeMembershipId);
        }
        finally
        {
            await RollbackIfActiveAsync(transaction);
            await DrainTaskAsync(reassignment);
        }
    }

    [Theory]
    [InlineData(TargetChange.MembershipDeactivation)]
    [InlineData(TargetChange.UserDeactivation)]
    public async Task ExecuteAsync_WhenReassignLocksTargetFirst_TargetChangeWaits(
        TargetChange targetChange)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask task = CreateTask(graph, "Assignment wins");
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        using var releaseDecision = new ManualResetEventSlim(false);
        var locksAcquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LegalTaskMutationPersistence persistence = CreatePersistence();
        var request = CreatePersistenceRequest(graph, task.Id);
        Task<LegalTaskMutationPersistenceResult>? reassignment = null;
        Task? deactivation = null;

        try
        {
            reassignment = Task.Run(
                () => persistence.ExecuteAsync(
                    request,
                    _ => graph.FirstTargetMembership.Id,
                    state =>
                    {
                        Assert.True(state.Assignee?.IsMembershipActive);
                        Assert.True(state.Assignee?.IsUserActive);
                        locksAcquired.TrySetResult(true);
                        releaseDecision.Wait(timeout.Token);
                        state.LegalTask.ChangeAssignee(
                            graph.FirstTargetMembership.Id);
                        return LegalTaskMutationDecision.Persist;
                    },
                    timeout.Token),
                timeout.Token);
            await locksAcquired.Task.WaitAsync(timeout.Token);
            deactivation = targetChange == TargetChange.MembershipDeactivation
                ? DeactivateMembershipAsync(
                    graph.FirstTargetMembership.Id,
                    timeout.Token)
                : DeactivateUserAsync(graph.FirstTargetUser.Id, timeout.Token);
            await WaitForBlockedUpdateAsync(
                targetChange == TargetChange.MembershipDeactivation
                    ? "organization_memberships"
                    : "users",
                timeout.Token);
            Assert.False(deactivation.IsCompleted);

            releaseDecision.Set();
            Assert.Equal(
                LegalTaskMutationPersistenceResult.Succeeded,
                await reassignment.WaitAsync(timeout.Token));
            await deactivation.WaitAsync(timeout.Token);

            Assert.Equal(
                graph.FirstTargetMembership.Id,
                (await FindTaskAsync(task.Id)).AssigneeMembershipId);
        }
        finally
        {
            releaseDecision.Set();
            await DrainTaskAsync(reassignment);
            await DrainTaskAsync(deactivation);
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenOwnerReassignsFirst_MemberUsesSerializedCurrentOwnership()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        LegalTask task = CreateTask(
            graph,
            "Member owned",
            assigneeMembershipId: graph.ActorMembership.Id,
            createdByMembershipId: graph.ActorMembership.Id);
        await SeedAsync(task);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        LegalTask locked = await LockTaskAsync(
            blockerContext,
            task.Id,
            graph.Organization.Id,
            timeout.Token);
        locked.ChangeAssignee(graph.FirstTargetMembership.Id);
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<UpdateLegalTaskResult>? update = null;

        try
        {
            update = UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                task.Id,
                "Stale owner update",
                timeout.Token);
            await WaitForBlockedSelectAsync("legal_tasks", timeout.Token);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(
                UpdateLegalTaskResult.AccessDenied,
                await update.WaitAsync(timeout.Token));
            LegalTask persisted = await FindTaskAsync(task.Id);
            Assert.Equal("Member owned", persisted.Title);
            Assert.Equal(
                graph.FirstTargetMembership.Id,
                persisted.AssigneeMembershipId);
        }
        finally
        {
            await RollbackIfActiveAsync(transaction);
            await DrainTaskAsync(update);
        }
    }

    [Fact]
    public async Task UpdateAsync_DifferentTaskAndActor_DoesNotUseTaskGlobalLock()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask lockedTask = CreateTask(graph, "Locked task");
        LegalTask unrelatedTask = CreateTask(
            graph,
            "Unrelated task",
            assigneeMembershipId: null,
            createdByMembershipId: graph.FirstTargetMembership.Id);
        await SeedAsync(lockedTask, unrelatedTask);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockTaskAsync(
            blockerContext,
            lockedTask.Id,
            graph.Organization.Id,
            timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var unrelatedGraph = graph with
        {
            ActorUser = graph.FirstTargetUser,
            ActorMembership = graph.FirstTargetMembership
        };

        try
        {
            UpdateLegalTaskResult result = await UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                unrelatedGraph,
                unrelatedTask.Id,
                "Independent update",
                timeout.Token).WaitAsync(timeout.Token);

            Assert.Equal(UpdateLegalTaskResult.Succeeded, result);
            Assert.Equal(
                "Independent update",
                (await FindTaskAsync(unrelatedTask.Id)).Title);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UpdateAsync_WrongTenantWhileTaskLocked_ReturnsNotFoundWithoutWaiting()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        var otherOrganization = new Organization(
            "Other concurrent tenant",
            "other-concurrent-tenant",
            SeededAt);
        var otherUser = new User(
            "Other tenant actor",
            "other-concurrent-actor@example.test",
            SeededAt);
        var otherMembership = new OrganizationMembership(
            otherOrganization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            SeededAt);
        var otherTask = new LegalTask(
            otherOrganization.Id,
            "Other locked task",
            null,
            null,
            null,
            null,
            otherMembership.Id,
            TaskCreatedAt);
        await SeedAsync(otherOrganization, otherUser, otherMembership, otherTask);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockTaskAsync(
            blockerContext,
            otherTask.Id,
            otherOrganization.Id,
            timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        try
        {
            UpdateLegalTaskResult result = await UpdateTitleAsync(
                CreateUpdateUseCase(queryContext),
                graph,
                otherTask.Id,
                "Cross-tenant mutation",
                timeout.Token).WaitAsync(timeout.Token);

            Assert.Equal(UpdateLegalTaskResult.NotFound, result);
            Assert.Equal(
                "Other locked task",
                (await FindTaskAsync(otherTask.Id)).Title);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private async Task ReopenFirstThenRunAsync<TResult>(
        TenantGraph graph,
        Guid taskId,
        Func<EnmaDbContext, Task<TResult>> runSecond,
        TResult expected,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, cancellationToken);
        LegalTask locked = await LockTaskAsync(
            blockerContext,
            taskId,
            graph.Organization.Id,
            cancellationToken);
        locked.Reopen();
        await blockerContext.SaveChangesAsync(cancellationToken);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<TResult> second = runSecond(queryContext);

        await WaitForBlockedSelectAsync("legal_tasks", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Assert.Equal(expected, await second.WaitAsync(cancellationToken));
    }

    private async Task HoldCompletedThenRunAsync<TResult>(
        TenantGraph graph,
        Guid taskId,
        Func<EnmaDbContext, Task<TResult>> runSecond,
        TResult expected,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await BeginTransactionAsync(blockerContext, cancellationToken);
        await LockTaskAsync(
            blockerContext,
            taskId,
            graph.Organization.Id,
            cancellationToken);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        Task<TResult> second = runSecond(queryContext);

        await WaitForBlockedSelectAsync("legal_tasks", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Assert.Equal(expected, await second.WaitAsync(cancellationToken));
    }

    private async Task WaitForBlockedSelectAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await WaitForBlockedQueryAsync(
            $"%FROM {tableName}%",
            "%FOR UPDATE%",
            cancellationToken);
    }

    private async Task WaitForBlockedUpdateAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        string pattern = $"%UPDATE {tableName}%";
        await WaitForBlockedQueryAsync(pattern, pattern, cancellationToken);
    }

    private async Task WaitForBlockedQueryAsync(
        string firstPattern,
        string secondPattern,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await observationContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE {firstPattern}
                  AND query ILIKE {secondPattern}
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private UpdateLegalTaskUseCase CreateUpdateUseCase(EnmaDbContext queryContext)
    {
        return new UpdateLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private ChangeLegalTaskAssigneeUseCase CreateAssignmentUseCase(
        EnmaDbContext queryContext)
    {
        return new ChangeLegalTaskAssigneeUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private CompleteLegalTaskUseCase CreateCompleteUseCase(EnmaDbContext queryContext)
    {
        return new CompleteLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence(),
            new FixedTimeProvider(SecondCompletedAt));
    }

    private ReopenLegalTaskUseCase CreateReopenUseCase(EnmaDbContext queryContext)
    {
        return new ReopenLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private static OrganizationAccessAuthorization CreateAccessAuthorization(
        EnmaDbContext queryContext)
    {
        return new OrganizationAccessAuthorization(
            new OrganizationAccessLookup(queryContext));
    }

    private LegalTaskMutationPersistence CreatePersistence()
    {
        var options = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        return new LegalTaskMutationPersistence(
            options,
            new FixedTimeProvider(SecondCompletedAt));
    }

    private static LegalTaskMutationPersistenceRequest CreatePersistenceRequest(
        TenantGraph graph,
        Guid taskId)
    {
        return new LegalTaskMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            taskId);
    }

    private static Task<UpdateLegalTaskResult> UpdateTitleAsync(
        UpdateLegalTaskUseCase useCase,
        TenantGraph graph,
        Guid taskId,
        string title,
        CancellationToken cancellationToken)
    {
        return useCase.ExecuteAsync(
            new UpdateLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                taskId,
                title,
                null,
                null,
                null),
            cancellationToken);
    }

    private static Task<ChangeLegalTaskAssigneeResult> ChangeAssigneeAsync(
        ChangeLegalTaskAssigneeUseCase useCase,
        TenantGraph graph,
        Guid taskId,
        Guid? assigneeMembershipId,
        CancellationToken cancellationToken)
    {
        return useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                taskId,
                assigneeMembershipId),
            cancellationToken);
    }

    private async Task ChangeActorAsync(
        Guid membershipId,
        Guid userId,
        ActorChange actorChange,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        if (actorChange == ActorChange.UserDeactivation)
        {
            User user = await dbContext.Users.SingleAsync(
                candidate => candidate.Id == userId,
                cancellationToken);
            user.Deactivate();
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == membershipId, cancellationToken);

        if (actorChange == ActorChange.RoleDemotion)
        {
            membership.ChangeRole(OrganizationRole.Member);
        }
        else
        {
            membership.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task DeactivateMembershipAsync(
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == membershipId, cancellationToken);
        membership.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task DeactivateUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.SingleAsync(
            candidate => candidate.Id == userId,
            cancellationToken);
        user.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TenantGraph> SeedTenantAsync(OrganizationRole actorRole)
    {
        var organization = new Organization(
            "Concurrent mutations",
            "concurrent-mutations",
            SeededAt);
        var actorUser = new User("Actor", "concurrent-b2-actor@example.test", SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            SeededAt);
        var firstTargetUser = new User(
            "First target",
            "concurrent-b2-target-a@example.test",
            SeededAt);
        var firstTargetMembership = new OrganizationMembership(
            organization.Id,
            firstTargetUser.Id,
            OrganizationRole.Member,
            SeededAt);
        var secondTargetUser = new User(
            "Second target",
            "concurrent-b2-target-b@example.test",
            SeededAt);
        var secondTargetMembership = new OrganizationMembership(
            organization.Id,
            secondTargetUser.Id,
            OrganizationRole.Member,
            SeededAt);
        await SeedAsync(
            organization,
            actorUser,
            actorMembership,
            firstTargetUser,
            firstTargetMembership,
            secondTargetUser,
            secondTargetMembership);

        return new TenantGraph(
            organization,
            actorUser,
            actorMembership,
            firstTargetUser,
            firstTargetMembership,
            secondTargetUser,
            secondTargetMembership);
    }

    private static LegalTask CreateTask(
        TenantGraph graph,
        string title,
        bool completed = false,
        Guid? assigneeMembershipId = null,
        Guid? createdByMembershipId = null)
    {
        var task = new LegalTask(
            graph.Organization.Id,
            title,
            null,
            null,
            null,
            assigneeMembershipId,
            createdByMembershipId ?? graph.ActorMembership.Id,
            TaskCreatedAt);

        if (completed)
        {
            task.Complete(FirstCompletedAt);
        }

        return task;
    }

    private static async Task<LegalTask> LockTaskAsync(
        EnmaDbContext dbContext,
        Guid taskId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalTasks.FromSqlInterpolated(
                $"""
                SELECT * FROM legal_tasks
                WHERE id = {taskId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """).ToListAsync(cancellationToken))
            .Single();
    }

    private async Task<LegalTask> FindTaskAsync(Guid taskId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks.AsNoTracking()
            .SingleAsync(task => task.Id == taskId);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Task<IDbContextTransaction> BeginTransactionAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
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

    public enum ActorChange
    {
        RoleDemotion = 0,
        MembershipDeactivation = 1,
        UserDeactivation = 2
    }

    public enum TargetChange
    {
        MembershipDeactivation = 0,
        UserDeactivation = 1
    }

    private sealed record TenantGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        User FirstTargetUser,
        OrganizationMembership FirstTargetMembership,
        User SecondTargetUser,
        OrganizationMembership SecondTargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
