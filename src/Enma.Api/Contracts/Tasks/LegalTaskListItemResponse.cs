namespace Enma.Api.Contracts.Tasks;

public sealed record LegalTaskListItemResponse(
    Guid Id,
    string Title,
    DateOnly? DueDate,
    Guid? ProcessId,
    string? ProcessTitle,
    string? ClientName,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName,
    Guid CreatedByMembershipId,
    LegalTaskStateResponse State,
    DateTimeOffset CreatedAt);
