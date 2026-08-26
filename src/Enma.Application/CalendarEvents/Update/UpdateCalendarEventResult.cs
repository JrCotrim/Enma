namespace Enma.Application.CalendarEvents.Update;

public enum UpdateCalendarEventResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedClientUnavailable = 2,
    RelatedProcessUnavailable = 3,
    RelatedAssigneeUnavailable = 4,
    InvalidInput = 5,
    Succeeded = 6
}
