using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Deadlines;
using Enma.Domain.Auditing;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDeadlineMutationPersistence
    : ILegalDeadlineMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalDeadlineMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<LegalDeadlineDetailsMutationPersistenceResult>
        UpdateDetailsAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
    {
        LegalDeadlineMutationOutcome outcome = await MutateAsync(
            request,
            NoRelatedMembership,
            decide,
            LegalDeadlineMutationOperation.UpdateDetails,
            cancellationToken);

        return outcome switch
        {
            LegalDeadlineMutationOutcome.AccessDenied =>
                LegalDeadlineDetailsMutationPersistenceResult.AccessDenied,
            LegalDeadlineMutationOutcome.NotFound =>
                LegalDeadlineDetailsMutationPersistenceResult.NotFound,
            LegalDeadlineMutationOutcome.Conflict =>
                LegalDeadlineDetailsMutationPersistenceResult.Conflict,
            LegalDeadlineMutationOutcome.Succeeded =>
                LegalDeadlineDetailsMutationPersistenceResult.Updated,
            _ => throw new InvalidOperationException(
                "Legal deadline mutation returned an invalid outcome.")
        };
    }

    public async Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        LegalDeadlineMutationOutcome outcome = await MutateAsync(
            request,
            NoRelatedMembership,
            decide,
            LegalDeadlineMutationOperation.Complete,
            cancellationToken);

        return MapLifecycleOutcome(outcome, allowConflict: false);
    }

    public async Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        LegalDeadlineMutationOutcome outcome = await MutateAsync(
            request,
            selectRelatedMembershipToLock,
            decide,
            LegalDeadlineMutationOperation.Reopen,
            cancellationToken);

        return MapLifecycleOutcome(outcome, allowConflict: true);
    }

    public async Task<LegalDeadlineResponsibleMutationPersistenceResult>
        ChangeResponsibleAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
    {
        LegalDeadlineMutationOutcome outcome = await MutateAsync(
            request,
            selectRelatedMembershipToLock,
            decide,
            LegalDeadlineMutationOperation.ChangeResponsible,
            cancellationToken);

        return outcome switch
        {
            LegalDeadlineMutationOutcome.AccessDenied =>
                LegalDeadlineResponsibleMutationPersistenceResult.AccessDenied,
            LegalDeadlineMutationOutcome.NotFound =>
                LegalDeadlineResponsibleMutationPersistenceResult.NotFound,
            LegalDeadlineMutationOutcome.RelatedResponsibleUnavailable =>
                LegalDeadlineResponsibleMutationPersistenceResult
                    .RelatedResponsibleUnavailable,
            LegalDeadlineMutationOutcome.Succeeded =>
                LegalDeadlineResponsibleMutationPersistenceResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal deadline responsible mutation returned an invalid outcome.")
        };
    }

    private static Guid? NoRelatedMembership(LegalDeadline legalDeadline)
    {
        return null;
    }

    private async Task<LegalDeadlineMutationOutcome> MutateAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        LegalDeadlineMutationOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectRelatedMembershipToLock);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.DeadlineId == Guid.Empty ||
            !Enum.IsDefined(operation))
        {
            return LegalDeadlineMutationOutcome.AccessDenied;
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalDeadline? legalDeadline = await LockDeadlineAsync(
            dbContext,
            request.DeadlineId,
            request.OrganizationId,
            cancellationToken);

        if (legalDeadline is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineMutationOutcome.NotFound;
        }

        string oldTitle = legalDeadline.Title;
        DateOnly oldDueDate = legalDeadline.DueDate;
        DateTimeOffset? oldCompletedAt = legalDeadline.CompletedAt;
        Guid? oldResponsibleMembershipId = legalDeadline.ResponsibleMembershipId;
        Guid? relatedMembershipId = selectRelatedMembershipToLock(legalDeadline);

        if (relatedMembershipId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A related membership lock selection cannot contain an empty id.");
        }

        IEnumerable<Guid> membershipIds = relatedMembershipId is Guid relatedId
            ? [request.ActorMembershipId, relatedId]
            : [request.ActorMembershipId];
        LegalTaskLockedIdentities identities = await LegalTaskIdentityLocking.LockAsync(
            dbContext,
            request.OrganizationId,
            membershipIds,
            cancellationToken);
        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        LegalDeadlineMutationDecision decision = decide(
            new LegalDeadlineMutationLockedState(
                legalDeadline,
                organization?.IsActive == true,
                CreateMemberState(request.ActorMembershipId, identities),
                relatedMembershipId is Guid lockedRelatedId
                    ? CreateMemberState(lockedRelatedId, identities)
                    : null));

        if (decision.Status == LegalDeadlineMutationDecisionStatus.AccessDenied)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineMutationOutcome.AccessDenied;
        }

        if (decision.Status == LegalDeadlineMutationDecisionStatus.Conflict)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineMutationOutcome.Conflict;
        }

        if (decision.Status ==
            LegalDeadlineMutationDecisionStatus.RelatedResponsibleUnavailable)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineMutationOutcome.RelatedResponsibleUnavailable;
        }

        if (decision.Status != LegalDeadlineMutationDecisionStatus.Persist)
        {
            throw new InvalidOperationException(
                "Legal deadline mutation returned an invalid decision.");
        }

        AuditIntent? auditIntent = CreateAuditIntent(
            legalDeadline,
            oldTitle,
            oldDueDate,
            oldCompletedAt,
            oldResponsibleMembershipId,
            operation);

        if (auditIntent is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return LegalDeadlineMutationOutcome.Succeeded;
        }

        if (!identities.MembershipsById.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership))
        {
            throw new InvalidOperationException(
                "A legal deadline mutation accepted a missing actor.");
        }

        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            auditIntent);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDeadlineMutationOutcome.Succeeded;
    }

    private static AuditIntent? CreateAuditIntent(
        LegalDeadline legalDeadline,
        string oldTitle,
        DateOnly oldDueDate,
        DateTimeOffset? oldCompletedAt,
        Guid? oldResponsibleMembershipId,
        LegalDeadlineMutationOperation operation)
    {
        bool responsibleChanged =
            legalDeadline.ResponsibleMembershipId != oldResponsibleMembershipId;

        if (responsibleChanged &&
            operation != LegalDeadlineMutationOperation.ChangeResponsible)
        {
            throw new InvalidOperationException(
                "A deadline decision changed the responsible member.");
        }

        if (operation == LegalDeadlineMutationOperation.ChangeResponsible)
        {
            if (!StringComparer.Ordinal.Equals(oldTitle, legalDeadline.Title) ||
                oldDueDate != legalDeadline.DueDate ||
                legalDeadline.CompletedAt != oldCompletedAt)
            {
                throw new InvalidOperationException(
                    "A deadline responsible decision changed another field group.");
            }

            return responsibleChanged
                ? new AuditIntent(
                    AuditEventType.LegalDeadlineResponsibleChanged,
                    legalDeadline.Id,
                    new LegalDeadlineResponsibleChangedAuditDetails(
                        oldResponsibleMembershipId,
                        legalDeadline.ResponsibleMembershipId))
                : null;
        }

        if (operation == LegalDeadlineMutationOperation.UpdateDetails)
        {
            if (legalDeadline.CompletedAt != oldCompletedAt)
            {
                throw new InvalidOperationException(
                    "A deadline details decision changed lifecycle state.");
            }

            var changedFields = new List<LegalDeadlineChangedField>(2);

            if (!StringComparer.Ordinal.Equals(oldTitle, legalDeadline.Title))
            {
                changedFields.Add(LegalDeadlineChangedField.Title);
            }

            if (oldDueDate != legalDeadline.DueDate)
            {
                changedFields.Add(LegalDeadlineChangedField.DueDate);
            }

            return changedFields.Count == 0
                ? null
                : new AuditIntent(
                    AuditEventType.LegalDeadlineDetailsChanged,
                    legalDeadline.Id,
                    new LegalDeadlineDetailsChangedAuditDetails(changedFields));
        }

        if (!StringComparer.Ordinal.Equals(oldTitle, legalDeadline.Title) ||
            oldDueDate != legalDeadline.DueDate)
        {
            throw new InvalidOperationException(
                "A deadline lifecycle decision changed deadline details.");
        }

        return operation switch
        {
            LegalDeadlineMutationOperation.Complete
                when legalDeadline.CompletedAt is null =>
                throw new InvalidOperationException(
                    "A deadline completion decision did not complete the deadline."),
            LegalDeadlineMutationOperation.Complete when oldCompletedAt is null =>
                new AuditIntent(
                    AuditEventType.LegalDeadlineCompleted,
                    legalDeadline.Id),
            LegalDeadlineMutationOperation.Complete => null,
            LegalDeadlineMutationOperation.Reopen
                when legalDeadline.CompletedAt is not null =>
                throw new InvalidOperationException(
                    "A deadline reopening decision did not reopen the deadline."),
            LegalDeadlineMutationOperation.Reopen when oldCompletedAt is not null =>
                new AuditIntent(
                    AuditEventType.LegalDeadlineReopened,
                    legalDeadline.Id),
            LegalDeadlineMutationOperation.Reopen => null,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static LegalDeadlineLifecycleMutationPersistenceResult
        MapLifecycleOutcome(
            LegalDeadlineMutationOutcome outcome,
            bool allowConflict)
    {
        return outcome switch
        {
            LegalDeadlineMutationOutcome.AccessDenied =>
                LegalDeadlineLifecycleMutationPersistenceResult.AccessDenied,
            LegalDeadlineMutationOutcome.NotFound =>
                LegalDeadlineLifecycleMutationPersistenceResult.NotFound,
            LegalDeadlineMutationOutcome.Succeeded =>
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
            LegalDeadlineMutationOutcome.Conflict when allowConflict =>
                LegalDeadlineLifecycleMutationPersistenceResult.Conflict,
            LegalDeadlineMutationOutcome.Conflict =>
                throw new InvalidOperationException(
                    "A deadline completion cannot return conflict."),
            _ => throw new InvalidOperationException(
                "Legal deadline mutation returned an invalid outcome.")
        };
    }

    private static LegalDeadlineLockedActorState? CreateMemberState(
        Guid membershipId,
        LegalTaskLockedIdentities identities)
    {
        if (!identities.MembershipsById.TryGetValue(
                membershipId,
                out OrganizationMembership? membership))
        {
            return null;
        }

        identities.UsersById.TryGetValue(membership.UserId, out User? user);
        return new LegalDeadlineLockedActorState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            user?.Id == membership.UserId && user.IsActive);
    }

    private static async Task<LegalDeadline?> LockDeadlineAsync(
        EnmaDbContext dbContext,
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalDeadlines
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_deadlines
                    WHERE id = {deadlineId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static Task<Organization?> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private enum LegalDeadlineMutationOperation
    {
        UpdateDetails = 0,
        Complete = 1,
        Reopen = 2,
        ChangeResponsible = 3
    }

    private enum LegalDeadlineMutationOutcome
    {
        AccessDenied = 0,
        NotFound = 1,
        Conflict = 2,
        Succeeded = 3,
        RelatedResponsibleUnavailable = 4
    }
}
