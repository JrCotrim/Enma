using Enma.Domain.Processes;

namespace Enma.Application.Processes;

public interface ILegalProcessOperationalMutationPersistence
{
    /// <summary>
    /// Locks the process, then the actor and the related membership selected from
    /// the locked process row (ordered by id), then their users, then the
    /// organization, and runs the decision against the locked state.
    /// </summary>
    Task<LegalProcessOperationalMutationPersistenceResult> ChangeOperationalAsync(
        LegalProcessOperationalMutationPersistenceRequest request,
        Func<LegalProcess, Guid?> selectRelatedMembershipToLock,
        Func<LegalProcessOperationalMutationLockedState,
            LegalProcessOperationalMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public enum LegalProcessOperationalMutation
{
    Details = 1,
    Status = 2,
    Responsible = 3
}

public sealed record LegalProcessOperationalMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ProcessId,
    LegalProcessOperationalMutation Mutation);

public sealed record LegalProcessOperationalMutationLockedState(
    LegalProcess LegalProcess,
    bool IsOrganizationActive,
    LegalProcessLockedActorState? Actor,
    LegalProcessLockedActorState? RelatedMember);

public enum LegalProcessOperationalMutationDecision
{
    AccessDenied = 0,
    Persist = 1,
    RelatedResponsibleUnavailable = 2,
    StatusTransitionNotAllowed = 3,
    CurrentResponsibleUnavailable = 4
}

public enum LegalProcessOperationalMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2,
    RelatedResponsibleUnavailable = 3,
    StatusTransitionNotAllowed = 4,
    CurrentResponsibleUnavailable = 5,
    DuplicateProcessNumber = 6
}
