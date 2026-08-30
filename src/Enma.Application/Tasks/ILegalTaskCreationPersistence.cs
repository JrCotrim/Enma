using Enma.Domain.Organizations;
using Enma.Domain.Tasks;

namespace Enma.Application.Tasks;

public interface ILegalTaskCreationPersistence
{
    Task<LegalTaskCreationPersistenceResult> ExecuteAsync(
        LegalTaskCreationPersistenceRequest request,
        Func<LegalTaskCreationLockedState, LegalTaskCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalTaskCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid? AssigneeMembershipId,
    Guid? ProcessId);

public sealed record LegalTaskCreationLockedState(
    bool IsOrganizationActive,
    LegalTaskCreationMemberState? Actor,
    LegalTaskCreationMemberState? Assignee);

public sealed record LegalTaskCreationMemberState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive);

public sealed class LegalTaskCreationDecision
{
    private LegalTaskCreationDecision(
        LegalTaskCreationDecisionStatus status,
        LegalTask? legalTask)
    {
        Status = status;
        LegalTask = legalTask;
    }

    public LegalTaskCreationDecisionStatus Status { get; }

    public LegalTask? LegalTask { get; }

    public static LegalTaskCreationDecision AccessDenied { get; } = new(
        LegalTaskCreationDecisionStatus.AccessDenied,
        null);

    public static LegalTaskCreationDecision RelatedAssigneeUnavailable { get; } = new(
        LegalTaskCreationDecisionStatus.RelatedAssigneeUnavailable,
        null);

    public static LegalTaskCreationDecision RelatedProcessUnavailable { get; } = new(
        LegalTaskCreationDecisionStatus.RelatedProcessUnavailable,
        null);

    public static LegalTaskCreationDecision InvalidInput { get; } = new(
        LegalTaskCreationDecisionStatus.InvalidInput,
        null);

    public static LegalTaskCreationDecision Persist(LegalTask legalTask)
    {
        ArgumentNullException.ThrowIfNull(legalTask);

        return new LegalTaskCreationDecision(
            LegalTaskCreationDecisionStatus.Persist,
            legalTask);
    }
}

public enum LegalTaskCreationDecisionStatus
{
    AccessDenied = 0,
    RelatedAssigneeUnavailable = 1,
    InvalidInput = 2,
    Persist = 3,
    RelatedProcessUnavailable = 4
}

public sealed class LegalTaskCreationPersistenceResult
{
    private LegalTaskCreationPersistenceResult(
        LegalTaskCreationDecisionStatus status,
        Guid? legalTaskId)
    {
        Status = status;
        LegalTaskId = legalTaskId;
    }

    public LegalTaskCreationDecisionStatus Status { get; }

    public Guid? LegalTaskId { get; }

    public static LegalTaskCreationPersistenceResult Rejected(
        LegalTaskCreationDecisionStatus status)
    {
        if (status == LegalTaskCreationDecisionStatus.Persist ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new LegalTaskCreationPersistenceResult(status, null);
    }

    public static LegalTaskCreationPersistenceResult Succeeded(Guid legalTaskId)
    {
        if (legalTaskId == Guid.Empty)
        {
            throw new ArgumentException(
                "Legal task id cannot be empty.",
                nameof(legalTaskId));
        }

        return new LegalTaskCreationPersistenceResult(
            LegalTaskCreationDecisionStatus.Persist,
            legalTaskId);
    }
}
