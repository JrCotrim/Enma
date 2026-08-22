using Enma.Application.Authorization;

namespace Enma.Application.CalendarEvents.Delete;

public sealed class DeleteCalendarEventUseCase
{
    private readonly CalendarEventAccessAuthorization _accessAuthorization;
    private readonly CalendarEventActionAuthorization _actionAuthorization;
    private readonly ICalendarEventMutationPersistence _mutationPersistence;

    public DeleteCalendarEventUseCase(
        CalendarEventAccessAuthorization accessAuthorization,
        CalendarEventActionAuthorization actionAuthorization,
        ICalendarEventMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _accessAuthorization = accessAuthorization;
        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<DeleteCalendarEventResult> ExecuteAsync(
        DeleteCalendarEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CalendarEventAccess? access = await CalendarEventUseCaseSupport.GetAccessAsync(
            _accessAuthorization,
            command.UserId,
            command.OrganizationId,
            cancellationToken);

        if (access is null)
        {
            return DeleteCalendarEventResult.AccessDenied;
        }

        if (command.CalendarEventId == Guid.Empty)
        {
            return DeleteCalendarEventResult.NotFound;
        }

        var request = new CalendarEventMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.CalendarEventId);

        CalendarEventMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                state => Decide(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            CalendarEventMutationPersistenceResult.AccessDenied =>
                DeleteCalendarEventResult.AccessDenied,
            CalendarEventMutationPersistenceResult.NotFound =>
                DeleteCalendarEventResult.NotFound,
            CalendarEventMutationPersistenceResult.Deleted =>
                DeleteCalendarEventResult.Deleted,
            _ => throw new InvalidOperationException(
                "Calendar event mutation persistence returned an invalid result.")
        };
    }

    private CalendarEventMutationDecision Decide(
        CalendarEventMutationPersistenceRequest request,
        CalendarEventMutationLockedState state)
    {
        if (!CalendarEventUseCaseSupport.IsAvailableActor(state, request))
        {
            return CalendarEventMutationDecision.AccessDenied;
        }

        var authorizationState = new CalendarEventAuthorizationState(
            state.CalendarEvent.CreatedByMembershipId);

        return _actionAuthorization.CanDelete(
            state.Actor!.Role,
            state.Actor.MembershipId,
            authorizationState)
                ? CalendarEventMutationDecision.Delete
                : CalendarEventMutationDecision.AccessDenied;
    }
}
