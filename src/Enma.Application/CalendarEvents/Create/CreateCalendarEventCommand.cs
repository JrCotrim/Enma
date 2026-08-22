namespace Enma.Application.CalendarEvents.Create;

public sealed record CreateCalendarEventCommand(
    Guid UserId,
    Guid OrganizationId,
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location,
    Guid? ClientId,
    Guid? ProcessId,
    Guid? AssigneeMembershipId);
