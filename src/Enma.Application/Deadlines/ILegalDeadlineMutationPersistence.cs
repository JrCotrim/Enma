using Enma.Domain.Deadlines;

namespace Enma.Application.Deadlines;

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
    LegalDeadlineLockedActorState? Actor);

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
}

public enum LegalDeadlineMutationDecisionStatus
{
    AccessDenied = 0,
    Conflict = 1,
    Persist = 2
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
    Succeeded = 2
}
