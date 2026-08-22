namespace Enma.Application.CalendarEvents.Assignment;

public enum ChangeCalendarEventAssigneeResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedAssigneeUnavailable = 2,
    InvalidInput = 3,
    Succeeded = 4
}
