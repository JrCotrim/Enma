namespace Enma.Application.Tasks;

public enum LegalTaskState
{
    Pending = 0,
    Completed = 1
}

public sealed record LegalTaskDetailReadModel(
    Guid Id,
    string Title,
    string? Description,
    DateOnly? DueDate,
    Guid? ProcessId,
    string? ProcessTitle,
    string? ClientName,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName,
    Guid CreatedByMembershipId,
    string CreatedByDisplayName,
    LegalTaskState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record LegalTaskListItem(
    Guid Id,
    string Title,
    DateOnly? DueDate,
    Guid? ProcessId,
    string? ProcessTitle,
    string? ClientName,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName,
    Guid CreatedByMembershipId,
    LegalTaskState State,
    DateTimeOffset CreatedAt);
