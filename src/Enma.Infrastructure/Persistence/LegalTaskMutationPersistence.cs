using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Tasks;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalTaskMutationPersistence : ILegalTaskMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalTaskMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public Task<LegalTaskMutationPersistenceResult> ExecuteAsync(
        LegalTaskMutationPersistenceRequest request,
        Func<LegalTaskMutationPreviewState, Guid?> selectAssigneeToLock,
        Func<LegalTaskMutationLockedState, LegalTaskMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectAssigneeToLock);
        ArgumentNullException.ThrowIfNull(decide);

        return ExecuteAttemptAsync(
            request,
            selectAssigneeToLock,
            decide,
            forcedAssigneeMembershipId: null,
            allowAssigneeRetry: true,
            cancellationToken);
    }

    private async Task<LegalTaskMutationPersistenceResult> ExecuteAttemptAsync(
        LegalTaskMutationPersistenceRequest request,
        Func<LegalTaskMutationPreviewState, Guid?> selectAssigneeToLock,
        Func<LegalTaskMutationLockedState, LegalTaskMutationDecision> decide,
        Guid? forcedAssigneeMembershipId,
        bool allowAssigneeRetry,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalTask? legalTask = await LockLegalTaskAsync(
            dbContext,
            request.LegalTaskId,
            request.OrganizationId,
            cancellationToken);

        if (legalTask is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalTaskMutationPersistenceResult.NotFound;
        }

        string oldTitle = legalTask.Title;
        string? oldDescription = legalTask.Description;
        DateOnly? oldDueDate = legalTask.DueDate;
        Guid? oldProcessId = legalTask.ProcessId;
        Guid? oldAssigneeMembershipId = legalTask.AssigneeMembershipId;
        DateTimeOffset? oldCompletedAt = legalTask.CompletedAt;

        LegalTaskMutationMemberState? previewActor =
            await LoadPreviewActorAsync(dbContext, request, cancellationToken);
        var previewState = new LegalTaskMutationPreviewState(
            LegalTaskMutationTaskState.From(legalTask),
            previewActor);
        Guid? assigneeMembershipId = forcedAssigneeMembershipId ??
            selectAssigneeToLock(previewState);

        if (assigneeMembershipId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "An assignee lock selection cannot contain an empty membership id.");
        }

        IEnumerable<Guid> membershipIds = assigneeMembershipId is Guid assigneeId
            ? [request.ActorMembershipId, assigneeId]
            : [request.ActorMembershipId];
        LegalTaskLockedIdentities identities = await LegalTaskIdentityLocking.LockAsync(
            dbContext,
            request.OrganizationId,
            membershipIds,
            cancellationToken);

        LegalTaskMutationMemberState? actor = CreateMemberState(
            request.ActorMembershipId,
            identities);
        LegalTaskMutationMemberState? assignee = assigneeMembershipId is Guid lockedId
            ? CreateMemberState(lockedId, identities)
            : null;
        var lockedState = new LegalTaskMutationLockedState(
            legalTask,
            actor,
            assigneeMembershipId is not null,
            assignee,
            null,
            null);

        LegalTaskMutationDecision decision = decide(lockedState);

        if (decision.Status == LegalTaskMutationDecisionStatus.LockAssignee)
        {
            if (!allowAssigneeRetry ||
                decision.RelationId is not Guid requestedAssigneeId)
            {
                throw new InvalidOperationException(
                    "Legal task mutation requested an invalid assignee lock retry.");
            }

            await transaction.RollbackAsync(cancellationToken);
            return await ExecuteAttemptAsync(
                request,
                selectAssigneeToLock,
                decide,
                requestedAssigneeId,
                allowAssigneeRetry: false,
                cancellationToken);
        }

        if (decision.Status == LegalTaskMutationDecisionStatus.ValidateProcess)
        {
            if (decision.RelationId is not Guid processId)
            {
                throw new InvalidOperationException(
                    "Legal task mutation requested invalid process validation.");
            }

            bool processExists = await dbContext.LegalProcesses
                .AsNoTracking()
                .AnyAsync(
                    process => process.Id == processId &&
                        process.OrganizationId == request.OrganizationId,
                    cancellationToken);

            decision = decide(lockedState with
            {
                ValidatedProcessId = processId,
                IsProcessAvailable = processExists
            });

            if (decision.Status is
                LegalTaskMutationDecisionStatus.ValidateProcess or
                LegalTaskMutationDecisionStatus.LockAssignee)
            {
                throw new InvalidOperationException(
                    "Legal task mutation requested repeated relation validation.");
            }
        }

        if (decision.Status != LegalTaskMutationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MapRejectedDecision(decision.Status);
        }

        OrganizationMembership actorMembership =
            identities.MembershipsById[request.ActorMembershipId];
        AppendAuditLogs(
            dbContext,
            actorMembership,
            legalTask,
            oldTitle,
            oldDescription,
            oldDueDate,
            oldProcessId,
            oldAssigneeMembershipId,
            oldCompletedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalTaskMutationPersistenceResult.Succeeded;
    }

    private void AppendAuditLogs(
        EnmaDbContext dbContext,
        OrganizationMembership actorMembership,
        LegalTask legalTask,
        string oldTitle,
        string? oldDescription,
        DateOnly? oldDueDate,
        Guid? oldProcessId,
        Guid? oldAssigneeMembershipId,
        DateTimeOffset? oldCompletedAt)
    {
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        var changedFields = new List<LegalTaskChangedField>(4);

        if (!StringComparer.Ordinal.Equals(oldTitle, legalTask.Title))
        {
            changedFields.Add(LegalTaskChangedField.Title);
        }

        if (!StringComparer.Ordinal.Equals(oldDescription, legalTask.Description))
        {
            changedFields.Add(LegalTaskChangedField.Description);
        }

        if (oldDueDate != legalTask.DueDate)
        {
            changedFields.Add(LegalTaskChangedField.DueDate);
        }

        if (oldProcessId != legalTask.ProcessId)
        {
            changedFields.Add(LegalTaskChangedField.ProcessId);
        }

        if (changedFields.Count > 0)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(
                    AuditEventType.LegalTaskDetailsChanged,
                    legalTask.Id,
                    new LegalTaskDetailsChangedAuditDetails(changedFields)));
        }

        if (oldAssigneeMembershipId != legalTask.AssigneeMembershipId)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(
                    AuditEventType.LegalTaskAssigneeChanged,
                    legalTask.Id,
                    new LegalTaskAssigneeChangedAuditDetails(
                        oldAssigneeMembershipId,
                        legalTask.AssigneeMembershipId)));
        }

        AuditEventType? lifecycleEvent = (oldCompletedAt, legalTask.CompletedAt) switch
        {
            (null, not null) => AuditEventType.LegalTaskCompleted,
            (not null, null) => AuditEventType.LegalTaskReopened,
            _ => null
        };

        if (lifecycleEvent is AuditEventType eventType)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(eventType, legalTask.Id));
        }
    }

    private static async Task<LegalTask?> LockLegalTaskAsync(
        EnmaDbContext dbContext,
        Guid legalTaskId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalTasks
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_tasks
                    WHERE id = {legalTaskId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static async Task<LegalTaskMutationMemberState?> LoadPreviewActorAsync(
        EnmaDbContext dbContext,
        LegalTaskMutationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        OrganizationMembership? membership = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.ActorMembershipId &&
                    candidate.OrganizationId == request.OrganizationId,
                cancellationToken);

        if (membership is null)
        {
            return null;
        }

        bool isUserActive = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == membership.UserId)
            .Select(user => (bool?)user.IsActive)
            .SingleOrDefaultAsync(cancellationToken) == true;

        return CreateMemberState(membership, isUserActive);
    }

    private static LegalTaskMutationMemberState? CreateMemberState(
        Guid membershipId,
        LegalTaskLockedIdentities identities)
    {
        if (!identities.MembershipsById.TryGetValue(
                membershipId,
                out OrganizationMembership? membership))
        {
            return null;
        }

        bool isUserActive = identities.UsersById.TryGetValue(
            membership.UserId,
            out User? user) && user.IsActive;

        return CreateMemberState(membership, isUserActive);
    }

    private static LegalTaskMutationMemberState CreateMemberState(
        OrganizationMembership membership,
        bool isUserActive)
    {
        return new LegalTaskMutationMemberState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            isUserActive);
    }

    private static LegalTaskMutationPersistenceResult MapRejectedDecision(
        LegalTaskMutationDecisionStatus status)
    {
        return status switch
        {
            LegalTaskMutationDecisionStatus.AccessDenied =>
                LegalTaskMutationPersistenceResult.AccessDenied,
            LegalTaskMutationDecisionStatus.RelatedProcessUnavailable =>
                LegalTaskMutationPersistenceResult.RelatedProcessUnavailable,
            LegalTaskMutationDecisionStatus.RelatedAssigneeUnavailable =>
                LegalTaskMutationPersistenceResult.RelatedAssigneeUnavailable,
            LegalTaskMutationDecisionStatus.InvalidInput =>
                LegalTaskMutationPersistenceResult.InvalidInput,
            LegalTaskMutationDecisionStatus.Conflict =>
                LegalTaskMutationPersistenceResult.Conflict,
            _ => throw new InvalidOperationException(
                "Legal task mutation returned an invalid rejection decision.")
        };
    }
}
