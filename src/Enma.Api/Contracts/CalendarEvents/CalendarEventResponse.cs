namespace Enma.Api.Contracts.CalendarEvents;

public sealed record CalendarEventResponse(
    Guid Id,
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location,
    Guid? ClientId,
    string? ClientName,
    Guid? ProcessId,
    string? ProcessTitle,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName,
    Guid CreatedByMembershipId,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAt);
