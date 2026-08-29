using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.Application.Deadlines;

public interface ILegalDeadlineCreationPersistence
{
    Task<LegalDeadlineCreationPersistenceResult> ExecuteAsync(
        LegalDeadlineCreationPersistenceRequest request,
        Func<LegalDeadlineCreationLockedState, LegalDeadlineCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalDeadlineCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ProcessId);

public sealed record LegalDeadlineCreationLockedState(
    bool IsOrganizationActive,
    LegalDeadlineLockedActorState? Actor,
    bool IsProcessAvailable);

public sealed record LegalDeadlineLockedActorState(
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

public sealed class LegalDeadlineCreationDecision
{
    private LegalDeadlineCreationDecision(
        LegalDeadlineCreationDecisionStatus status,
        LegalDeadline? legalDeadline)
    {
        Status = status;
        LegalDeadline = legalDeadline;
    }

    public LegalDeadlineCreationDecisionStatus Status { get; }

    public LegalDeadline? LegalDeadline { get; }

    public static LegalDeadlineCreationDecision AccessDenied { get; } = new(
        LegalDeadlineCreationDecisionStatus.AccessDenied,
        null);

    public static LegalDeadlineCreationDecision RelatedProcessUnavailable { get; } =
        new(
            LegalDeadlineCreationDecisionStatus.RelatedProcessUnavailable,
            null);

    public static LegalDeadlineCreationDecision Persist(
        LegalDeadline legalDeadline)
    {
        ArgumentNullException.ThrowIfNull(legalDeadline);

        return new LegalDeadlineCreationDecision(
            LegalDeadlineCreationDecisionStatus.Persist,
            legalDeadline);
    }
}

public enum LegalDeadlineCreationDecisionStatus
{
    AccessDenied = 0,
    RelatedProcessUnavailable = 1,
    Persist = 2
}

public sealed class LegalDeadlineCreationPersistenceResult
{
    private LegalDeadlineCreationPersistenceResult(
        LegalDeadlineCreationDecisionStatus status,
        Guid? deadlineId)
    {
        Status = status;
        DeadlineId = deadlineId;
    }

    public LegalDeadlineCreationDecisionStatus Status { get; }

    public Guid? DeadlineId { get; }

    public static LegalDeadlineCreationPersistenceResult Rejected(
        LegalDeadlineCreationDecisionStatus status)
    {
        if (status == LegalDeadlineCreationDecisionStatus.Persist ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new LegalDeadlineCreationPersistenceResult(status, null);
    }

    public static LegalDeadlineCreationPersistenceResult Created(Guid deadlineId)
    {
        if (deadlineId == Guid.Empty)
        {
            throw new ArgumentException(
                "Legal deadline id cannot be empty.",
                nameof(deadlineId));
        }

        return new LegalDeadlineCreationPersistenceResult(
            LegalDeadlineCreationDecisionStatus.Persist,
            deadlineId);
    }
}
