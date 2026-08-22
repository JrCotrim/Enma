namespace Enma.Application.CalendarEvents;

public interface ICalendarEventReadQueries
{
    Task<CalendarEventDetailReadModel?> FindAsync(
        Guid calendarEventId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
