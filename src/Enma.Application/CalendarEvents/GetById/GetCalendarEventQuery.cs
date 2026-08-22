namespace Enma.Application.CalendarEvents.GetById;

public sealed record GetCalendarEventQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid CalendarEventId);
