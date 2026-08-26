using Enma.Application.Authorization;

namespace Enma.Application.CalendarEvents.Update;

public sealed class UpdateCalendarEventUseCase
{
    private readonly CalendarEventAccessAuthorization _accessAuthorization;
    private readonly CalendarEventActionAuthorization _actionAuthorization;
    private readonly ICalendarEventMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public UpdateCalendarEventUseCase(
        CalendarEventAccessAuthorization accessAuthorization,
        CalendarEventActionAuthorization actionAuthorization,
        ICalendarEventMutationPersistence mutationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _accessAuthorization = accessAuthorization;
        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<UpdateCalendarEventResult> ExecuteAsync(
        UpdateCalendarEventCommand command,
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
            return UpdateCalendarEventResult.AccessDenied;
        }

        if (command.CalendarEventId == Guid.Empty)
        {
            return UpdateCalendarEventResult.NotFound;
        }

        if (HasInvalidInput(command))
        {
            return UpdateCalendarEventResult.InvalidInput;
        }

        var request = new CalendarEventMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.CalendarEventId);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();

        CalendarEventMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                state => Decide(command, request, state, nowUtc),
                cancellationToken);

        return persistenceResult switch
        {
            CalendarEventMutationPersistenceResult.AccessDenied =>
                UpdateCalendarEventResult.AccessDenied,
            CalendarEventMutationPersistenceResult.NotFound =>
                UpdateCalendarEventResult.NotFound,
            CalendarEventMutationPersistenceResult.RelatedClientUnavailable =>
                UpdateCalendarEventResult.RelatedClientUnavailable,
            CalendarEventMutationPersistenceResult.RelatedProcessUnavailable =>
                UpdateCalendarEventResult.RelatedProcessUnavailable,
            CalendarEventMutationPersistenceResult.RelatedAssigneeUnavailable =>
                UpdateCalendarEventResult.RelatedAssigneeUnavailable,
            CalendarEventMutationPersistenceResult.InvalidInput =>
                UpdateCalendarEventResult.InvalidInput,
            CalendarEventMutationPersistenceResult.Succeeded =>
                UpdateCalendarEventResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Calendar event mutation persistence returned an invalid result.")
        };
    }

    private CalendarEventMutationDecision Decide(
        UpdateCalendarEventCommand command,
        CalendarEventMutationPersistenceRequest request,
        CalendarEventMutationLockedState state,
        DateTimeOffset nowUtc)
    {
        if (!CalendarEventUseCaseSupport.IsAvailableActor(state, request))
        {
            return CalendarEventMutationDecision.AccessDenied;
        }

        var authorizationState = new CalendarEventAuthorizationState(
            state.CalendarEvent.CreatedByMembershipId);

        if (!_actionAuthorization.CanUpdate(
                state.Actor!.Role,
                state.Actor.MembershipId,
                authorizationState))
        {
            return CalendarEventMutationDecision.AccessDenied;
        }

        if (command.ClientId is not null || command.ProcessId is not null)
        {
            if (!state.AssociationLookupPerformed)
            {
                return CalendarEventMutationDecision.ValidateAssociation(
                    command.ClientId,
                    command.ProcessId);
            }

            if (command.ClientId is Guid clientId &&
                (state.ValidatedClientId != clientId ||
                    state.IsClientAvailable != true))
            {
                return CalendarEventMutationDecision.RelatedClientUnavailable;
            }

            if (command.ProcessId is Guid processId &&
                (state.ValidatedProcessId != processId ||
                    state.IsProcessAvailable != true))
            {
                return CalendarEventMutationDecision.RelatedProcessUnavailable;
            }
        }

        if (command.EndsAt.ToUniversalTime() > nowUtc &&
            state.CalendarEvent.AssigneeMembershipId is Guid assigneeMembershipId)
        {
            if (!state.AssigneeLookupPerformed)
            {
                return CalendarEventMutationDecision.ValidateAssignee(
                    assigneeMembershipId);
            }

            if (state.ValidatedAssigneeMembershipId != assigneeMembershipId ||
                !CalendarEventUseCaseSupport.IsAvailableAssignee(
                    state.Assignee,
                    request.OrganizationId,
                    assigneeMembershipId))
            {
                return CalendarEventMutationDecision.RelatedAssigneeUnavailable;
            }
        }

        try
        {
            state.CalendarEvent.Reschedule(command.StartsAt, command.EndsAt);
            state.CalendarEvent.ChangeAssociation(
                command.ClientId,
                command.ProcessId);
            state.CalendarEvent.ChangeDetails(
                command.Title,
                command.Description,
                command.Location);

            return CalendarEventMutationDecision.Persist;
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or
                "description" or
                "startsAt" or
                "endsAt" or
                "location" or
                "clientId" or
                "processId")
        {
            return CalendarEventMutationDecision.InvalidInput;
        }
    }

    private static bool HasInvalidInput(UpdateCalendarEventCommand command)
    {
        return command.ClientId == Guid.Empty ||
            command.ProcessId == Guid.Empty ||
            command.ClientId is not null && command.ProcessId is not null ||
            command.StartsAt == DateTimeOffset.MinValue ||
            command.EndsAt == DateTimeOffset.MinValue ||
            command.EndsAt <= command.StartsAt;
    }
}
