namespace Enma.Application.CalendarEvents.Create;

public sealed class CreateCalendarEventResult
{
    private CreateCalendarEventResult(
        CreateCalendarEventResultStatus status,
        Guid? calendarEventId)
    {
        Status = status;
        CalendarEventId = calendarEventId;
    }

    public CreateCalendarEventResultStatus Status { get; }

    public Guid? CalendarEventId { get; }

    public static CreateCalendarEventResult AccessDenied { get; } = new(
        CreateCalendarEventResultStatus.AccessDenied,
        null);

    public static CreateCalendarEventResult RelatedClientUnavailable { get; } =
        new(CreateCalendarEventResultStatus.RelatedClientUnavailable, null);

    public static CreateCalendarEventResult RelatedProcessUnavailable { get; } =
        new(CreateCalendarEventResultStatus.RelatedProcessUnavailable, null);

    public static CreateCalendarEventResult RelatedAssigneeUnavailable { get; } =
        new(CreateCalendarEventResultStatus.RelatedAssigneeUnavailable, null);

    public static CreateCalendarEventResult InvalidInput { get; } = new(
        CreateCalendarEventResultStatus.InvalidInput,
        null);

    public static CreateCalendarEventResult Created(Guid calendarEventId)
    {
        if (calendarEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "Calendar event id cannot be empty.",
                nameof(calendarEventId));
        }

        return new CreateCalendarEventResult(
            CreateCalendarEventResultStatus.Created,
            calendarEventId);
    }
}

public enum CreateCalendarEventResultStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    RelatedProcessUnavailable = 2,
    RelatedAssigneeUnavailable = 3,
    InvalidInput = 4,
    Created = 5
}
