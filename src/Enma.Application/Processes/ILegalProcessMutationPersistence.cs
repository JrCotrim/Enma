using Enma.Domain.Processes;

namespace Enma.Application.Processes;

public interface ILegalProcessMutationPersistence
{
    Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        LegalProcessMutationPersistenceRequest request,
        Func<LegalProcessMutationLockedState, LegalProcessMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalProcessMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ProcessId);

public sealed record LegalProcessMutationLockedState(
    LegalProcess LegalProcess,
    bool IsOrganizationActive,
    LegalProcessLockedActorState? Actor);

public sealed class LegalProcessMutationDecision
{
    private LegalProcessMutationDecision(
        LegalProcessMutationDecisionStatus status)
    {
        Status = status;
    }

    public LegalProcessMutationDecisionStatus Status { get; }

    public static LegalProcessMutationDecision AccessDenied { get; } = new(
        LegalProcessMutationDecisionStatus.AccessDenied);

    public static LegalProcessMutationDecision Persist { get; } = new(
        LegalProcessMutationDecisionStatus.Persist);
}

public enum LegalProcessMutationDecisionStatus
{
    AccessDenied = 0,
    Persist = 1
}

public enum LegalProcessMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Updated = 2
}
