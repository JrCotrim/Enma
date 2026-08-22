namespace Enma.Application.CalendarEvents.Update;

public sealed record UpdateCalendarEventCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid CalendarEventId,
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location,
    Guid? ClientId,
    Guid? ProcessId);
