using Enma.Domain.Deadlines;

namespace Enma.Application.Deadlines;

/// <summary>
/// Every mutation locks the deadline, then the actor and the related membership
/// selected from the locked deadline row (ordered by id), then their users, then
/// the organization, and runs the decision against the locked state.
/// </summary>
public interface ILegalDeadlineMutationPersistence
{
    Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default);

    Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default);

    Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default);

    Task<LegalDeadlineResponsibleMutationPersistenceResult> ChangeResponsibleAsync(
        LegalDeadlineMutationPersistenceRequest request,
        Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
        Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalDeadlineMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid DeadlineId);

public sealed record LegalDeadlineMutationLockedState(
    LegalDeadline LegalDeadline,
    bool IsOrganizationActive,
    LegalDeadlineLockedActorState? Actor,
    LegalDeadlineLockedActorState? RelatedMember = null);

public sealed class LegalDeadlineMutationDecision
{
    private LegalDeadlineMutationDecision(
        LegalDeadlineMutationDecisionStatus status)
    {
        Status = status;
    }

    public LegalDeadlineMutationDecisionStatus Status { get; }

    public static LegalDeadlineMutationDecision AccessDenied { get; } = new(
        LegalDeadlineMutationDecisionStatus.AccessDenied);

    public static LegalDeadlineMutationDecision Conflict { get; } = new(
        LegalDeadlineMutationDecisionStatus.Conflict);

    public static LegalDeadlineMutationDecision Persist { get; } = new(
        LegalDeadlineMutationDecisionStatus.Persist);

    public static LegalDeadlineMutationDecision RelatedResponsibleUnavailable { get; } =
        new(LegalDeadlineMutationDecisionStatus.RelatedResponsibleUnavailable);
}

public enum LegalDeadlineMutationDecisionStatus
{
    AccessDenied = 0,
    Conflict = 1,
    Persist = 2,
    RelatedResponsibleUnavailable = 3
}

public enum LegalDeadlineDetailsMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Conflict = 2,
    Updated = 3
}

public enum LegalDeadlineLifecycleMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2,
    Conflict = 3
}

public enum LegalDeadlineResponsibleMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedResponsibleUnavailable = 2,
    Succeeded = 3
}
