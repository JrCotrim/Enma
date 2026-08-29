using Enma.Domain.Processes;
using Enma.Domain.Organizations;

namespace Enma.Application.Processes;

public interface ILegalProcessCreationPersistence
{
    Task<LegalProcessCreationPersistenceResult> ExecuteAsync(
        LegalProcessCreationPersistenceRequest request,
        Func<LegalProcessCreationLockedState, LegalProcessCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalProcessCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ClientId);

public sealed record LegalProcessCreationLockedState(
    bool IsOrganizationActive,
    LegalProcessLockedActorState? Actor,
    bool IsClientAvailable);

public sealed record LegalProcessLockedActorState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive)
{
    public bool IsAvailableFor(
        Guid userId,
        Guid organizationId,
        Guid membershipId)
    {
        return MembershipId == membershipId &&
            OrganizationId == organizationId &&
            UserId == userId &&
            IsMembershipActive &&
            IsUserActive &&
            Enum.IsDefined(Role);
    }
}

public sealed class LegalProcessCreationDecision
{
    private LegalProcessCreationDecision(
        LegalProcessCreationDecisionStatus status,
        LegalProcess? legalProcess)
    {
        Status = status;
        LegalProcess = legalProcess;
    }

    public LegalProcessCreationDecisionStatus Status { get; }

    public LegalProcess? LegalProcess { get; }

    public static LegalProcessCreationDecision AccessDenied { get; } = new(
        LegalProcessCreationDecisionStatus.AccessDenied,
        null);

    public static LegalProcessCreationDecision RelatedClientUnavailable { get; } =
        new(
            LegalProcessCreationDecisionStatus.RelatedClientUnavailable,
            null);

    public static LegalProcessCreationDecision Persist(LegalProcess legalProcess)
    {
        ArgumentNullException.ThrowIfNull(legalProcess);

        return new LegalProcessCreationDecision(
            LegalProcessCreationDecisionStatus.Persist,
            legalProcess);
    }
}

public enum LegalProcessCreationDecisionStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    Persist = 2
}

public sealed class LegalProcessCreationPersistenceResult
{
    private LegalProcessCreationPersistenceResult(
        LegalProcessCreationDecisionStatus status,
        Guid? processId)
    {
        Status = status;
        ProcessId = processId;
    }

    public LegalProcessCreationDecisionStatus Status { get; }

    public Guid? ProcessId { get; }

    public static LegalProcessCreationPersistenceResult Rejected(
        LegalProcessCreationDecisionStatus status)
    {
        if (status == LegalProcessCreationDecisionStatus.Persist ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new LegalProcessCreationPersistenceResult(status, null);
    }

    public static LegalProcessCreationPersistenceResult Created(Guid processId)
    {
        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                "Legal process id cannot be empty.",
                nameof(processId));
        }

        return new LegalProcessCreationPersistenceResult(
            LegalProcessCreationDecisionStatus.Persist,
            processId);
    }
}
