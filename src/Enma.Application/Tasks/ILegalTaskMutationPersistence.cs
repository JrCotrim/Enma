using Enma.Domain.Organizations;
using Enma.Domain.Tasks;

namespace Enma.Application.Tasks;

public interface ILegalTaskMutationPersistence
{
    Task<LegalTaskMutationPersistenceResult> ExecuteAsync(
        LegalTaskMutationPersistenceRequest request,
        Func<LegalTaskMutationPreviewState, Guid?> selectAssigneeToLock,
        Func<LegalTaskMutationLockedState, LegalTaskMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record LegalTaskMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid LegalTaskId);

public sealed record LegalTaskMutationTaskState(
    Guid Id,
    Guid OrganizationId,
    Guid? ProcessId,
    Guid? AssigneeMembershipId,
    Guid CreatedByMembershipId,
    DateTimeOffset? CompletedAt)
{
    public static LegalTaskMutationTaskState From(LegalTask legalTask)
    {
        ArgumentNullException.ThrowIfNull(legalTask);

        return new LegalTaskMutationTaskState(
            legalTask.Id,
            legalTask.OrganizationId,
            legalTask.ProcessId,
            legalTask.AssigneeMembershipId,
            legalTask.CreatedByMembershipId,
            legalTask.CompletedAt);
    }
}

public sealed record LegalTaskMutationMemberState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive);

public sealed record LegalTaskMutationPreviewState(
    LegalTaskMutationTaskState LegalTask,
    LegalTaskMutationMemberState? Actor);

public sealed record LegalTaskMutationLockedState(
    LegalTask LegalTask,
    LegalTaskMutationMemberState? Actor,
    bool AssigneeLookupPerformed,
    LegalTaskMutationMemberState? Assignee,
    Guid? ValidatedProcessId,
    bool? IsProcessAvailable);

public sealed class LegalTaskMutationDecision
{
    private LegalTaskMutationDecision(
        LegalTaskMutationDecisionStatus status,
        Guid? relationId)
    {
        Status = status;
        RelationId = relationId;
    }

    public LegalTaskMutationDecisionStatus Status { get; }

    public Guid? RelationId { get; }

    public static LegalTaskMutationDecision AccessDenied { get; } = new(
        LegalTaskMutationDecisionStatus.AccessDenied,
        null);

    public static LegalTaskMutationDecision RelatedProcessUnavailable { get; } = new(
        LegalTaskMutationDecisionStatus.RelatedProcessUnavailable,
        null);

    public static LegalTaskMutationDecision RelatedAssigneeUnavailable { get; } = new(
        LegalTaskMutationDecisionStatus.RelatedAssigneeUnavailable,
        null);

    public static LegalTaskMutationDecision InvalidInput { get; } = new(
        LegalTaskMutationDecisionStatus.InvalidInput,
        null);

    public static LegalTaskMutationDecision Conflict { get; } = new(
        LegalTaskMutationDecisionStatus.Conflict,
        null);

    public static LegalTaskMutationDecision Persist { get; } = new(
        LegalTaskMutationDecisionStatus.Persist,
        null);

    public static LegalTaskMutationDecision ValidateProcess(Guid processId)
    {
        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                "Process id cannot be empty.",
                nameof(processId));
        }

        return new LegalTaskMutationDecision(
            LegalTaskMutationDecisionStatus.ValidateProcess,
            processId);
    }

    public static LegalTaskMutationDecision LockAssignee(Guid membershipId)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException(
                "Membership id cannot be empty.",
                nameof(membershipId));
        }

        return new LegalTaskMutationDecision(
            LegalTaskMutationDecisionStatus.LockAssignee,
            membershipId);
    }
}

public enum LegalTaskMutationDecisionStatus
{
    AccessDenied = 0,
    RelatedProcessUnavailable = 1,
    RelatedAssigneeUnavailable = 2,
    InvalidInput = 3,
    Conflict = 4,
    ValidateProcess = 5,
    LockAssignee = 6,
    Persist = 7
}

public enum LegalTaskMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedProcessUnavailable = 2,
    RelatedAssigneeUnavailable = 3,
    InvalidInput = 4,
    Conflict = 5,
    Succeeded = 6
}
