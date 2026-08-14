namespace Enma.Api.Contracts.Tasks;

public sealed record LegalTaskResponse(
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
    LegalTaskStateResponse State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
