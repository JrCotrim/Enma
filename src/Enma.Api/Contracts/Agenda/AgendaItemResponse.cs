namespace Enma.Api.Contracts.Agenda;

public sealed record AgendaItemResponse(
    AgendaItemKindResponse Kind,
    Guid Id,
    string Title,
    bool IsAllDay,
    DateOnly? Date,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? CompletedAt,
    Guid? ClientId,
    string? ClientName,
    Guid? ProcessId,
    string? ProcessTitle,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName);
