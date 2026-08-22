using Enma.Application.Authorization;

namespace Enma.Application.CalendarEvents.GetById;

public sealed class GetCalendarEventUseCase
{
    private readonly CalendarEventAccessAuthorization _accessAuthorization;
    private readonly CalendarEventActionAuthorization _actionAuthorization;
    private readonly ICalendarEventReadQueries _readQueries;

    public GetCalendarEventUseCase(
        CalendarEventAccessAuthorization accessAuthorization,
        CalendarEventActionAuthorization actionAuthorization,
        ICalendarEventReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _accessAuthorization = accessAuthorization;
        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetCalendarEventResult> ExecuteAsync(
        GetCalendarEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        CalendarEventAccess? access = await CalendarEventUseCaseSupport.GetAccessAsync(
            _accessAuthorization,
            query.UserId,
            query.OrganizationId,
            cancellationToken);

        if (access is null || !_actionAuthorization.CanView(access.Role))
        {
            return GetCalendarEventResult.AccessDenied;
        }

        if (query.CalendarEventId == Guid.Empty)
        {
            return GetCalendarEventResult.InvalidInput;
        }

        CalendarEventDetailReadModel? calendarEvent =
            await _readQueries.FindAsync(
                query.CalendarEventId,
                access.OrganizationId,
                cancellationToken);

        return calendarEvent is null
            ? GetCalendarEventResult.NotFound
            : GetCalendarEventResult.Succeeded(calendarEvent);
    }
}
