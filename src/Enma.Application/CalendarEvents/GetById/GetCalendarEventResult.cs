namespace Enma.Application.CalendarEvents.GetById;

public sealed class GetCalendarEventResult
{
    private GetCalendarEventResult(
        GetCalendarEventResultStatus status,
        CalendarEventDetailReadModel? calendarEvent)
    {
        Status = status;
        CalendarEvent = calendarEvent;
    }

    public GetCalendarEventResultStatus Status { get; }

    public CalendarEventDetailReadModel? CalendarEvent { get; }

    public static GetCalendarEventResult AccessDenied { get; } = new(
        GetCalendarEventResultStatus.AccessDenied,
        null);

    public static GetCalendarEventResult NotFound { get; } = new(
        GetCalendarEventResultStatus.NotFound,
        null);

    public static GetCalendarEventResult InvalidInput { get; } = new(
        GetCalendarEventResultStatus.InvalidInput,
        null);

    public static GetCalendarEventResult Succeeded(
        CalendarEventDetailReadModel calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return new GetCalendarEventResult(
            GetCalendarEventResultStatus.Succeeded,
            calendarEvent);
    }
}

public enum GetCalendarEventResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    Succeeded = 3
}
