using Enma.Application.Authorization;

namespace Enma.Application.CalendarEvents.Assignment;

public sealed class ChangeCalendarEventAssigneeUseCase
{
    private readonly CalendarEventAccessAuthorization _accessAuthorization;
    private readonly CalendarEventActionAuthorization _actionAuthorization;
    private readonly ICalendarEventMutationPersistence _mutationPersistence;

    public ChangeCalendarEventAssigneeUseCase(
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

    public async Task<ChangeCalendarEventAssigneeResult> ExecuteAsync(
        ChangeCalendarEventAssigneeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CalendarEventAccess? access = await CalendarEventUseCaseSupport.GetAccessAsync(
            _accessAuthorization,
            command.UserId,
            command.OrganizationId,
            cancellationToken);

        if (access is null ||
            !_actionAuthorization.CanRequestAssigneeChange(
                access.Role,
                access.MembershipId,
                command.AssigneeMembershipId))
        {
            return ChangeCalendarEventAssigneeResult.AccessDenied;
        }

        if (command.CalendarEventId == Guid.Empty)
        {
            return ChangeCalendarEventAssigneeResult.NotFound;
        }

        if (command.AssigneeMembershipId == Guid.Empty)
        {
            return ChangeCalendarEventAssigneeResult.InvalidInput;
        }

        var request = new CalendarEventMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.CalendarEventId);

        CalendarEventMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                state => Decide(command, request, state),
                cancellationToken);

        return persistenceResult switch
        {
            CalendarEventMutationPersistenceResult.AccessDenied =>
                ChangeCalendarEventAssigneeResult.AccessDenied,
            CalendarEventMutationPersistenceResult.NotFound =>
                ChangeCalendarEventAssigneeResult.NotFound,
            CalendarEventMutationPersistenceResult.RelatedAssigneeUnavailable =>
                ChangeCalendarEventAssigneeResult.RelatedAssigneeUnavailable,
            CalendarEventMutationPersistenceResult.InvalidInput =>
                ChangeCalendarEventAssigneeResult.InvalidInput,
            CalendarEventMutationPersistenceResult.Succeeded =>
                ChangeCalendarEventAssigneeResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Calendar event mutation persistence returned an invalid result.")
        };
    }

    private CalendarEventMutationDecision Decide(
        ChangeCalendarEventAssigneeCommand command,
        CalendarEventMutationPersistenceRequest request,
        CalendarEventMutationLockedState state)
    {
        if (!CalendarEventUseCaseSupport.IsAvailableActor(state, request))
        {
            return CalendarEventMutationDecision.AccessDenied;
        }

        var authorizationState = new CalendarEventAuthorizationState(
            state.CalendarEvent.CreatedByMembershipId);

        if (!_actionAuthorization.CanChangeAssignee(
                state.Actor!.Role,
                state.Actor.MembershipId,
                authorizationState,
                command.AssigneeMembershipId))
        {
            return CalendarEventMutationDecision.AccessDenied;
        }

        if (command.AssigneeMembershipId is Guid assigneeMembershipId)
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
            state.CalendarEvent.ChangeAssignee(command.AssigneeMembershipId);
            return CalendarEventMutationDecision.Persist;
        }
        catch (ArgumentException exception) when (
            exception.ParamName == "assigneeMembershipId")
        {
            return CalendarEventMutationDecision.InvalidInput;
        }
    }
}
