namespace Enma.Application.CalendarEvents.Delete;

public sealed record DeleteCalendarEventCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid CalendarEventId);
