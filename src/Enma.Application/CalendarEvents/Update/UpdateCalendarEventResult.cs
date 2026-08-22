namespace Enma.Application.CalendarEvents.Update;

public enum UpdateCalendarEventResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedClientUnavailable = 2,
    RelatedProcessUnavailable = 3,
    InvalidInput = 4,
    Succeeded = 5
}
