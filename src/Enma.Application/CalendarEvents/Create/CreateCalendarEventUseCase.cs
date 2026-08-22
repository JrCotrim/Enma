using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;

namespace Enma.Application.CalendarEvents.Create;

public sealed class CreateCalendarEventUseCase
{
    private readonly CalendarEventAccessAuthorization _accessAuthorization;
    private readonly CalendarEventActionAuthorization _actionAuthorization;
    private readonly IActiveClientInOrganizationLookup _activeClientLookup;
    private readonly IProcessOrganizationOwnershipLookup _processOwnershipLookup;
    private readonly ICalendarEventCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateCalendarEventUseCase(
        CalendarEventAccessAuthorization accessAuthorization,
        CalendarEventActionAuthorization actionAuthorization,
        IActiveClientInOrganizationLookup activeClientLookup,
        IProcessOrganizationOwnershipLookup processOwnershipLookup,
        ICalendarEventCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(activeClientLookup);
        ArgumentNullException.ThrowIfNull(processOwnershipLookup);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _accessAuthorization = accessAuthorization;
        _actionAuthorization = actionAuthorization;
        _activeClientLookup = activeClientLookup;
        _processOwnershipLookup = processOwnershipLookup;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreateCalendarEventResult> ExecuteAsync(
        CreateCalendarEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CalendarEventAccessAuthorizationResult access =
            await _accessAuthorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (!TryGetAccess(access, command, out CalendarEventCreationAccess actor))
        {
            return CreateCalendarEventResult.AccessDenied;
        }

        if (!_actionAuthorization.CanCreate(actor.Role) ||
            !_actionAuthorization.CanAssignDuringCreate(
                actor.Role,
                actor.MembershipId,
                command.AssigneeMembershipId))
        {
            return CreateCalendarEventResult.AccessDenied;
        }

        if (HasInvalidInput(command))
        {
            return CreateCalendarEventResult.InvalidInput;
        }

        if (command.ClientId is Guid clientId &&
            !await _activeClientLookup.ExistsAsync(
                clientId,
                actor.OrganizationId,
                cancellationToken))
        {
            return CreateCalendarEventResult.RelatedClientUnavailable;
        }

        if (command.ProcessId is Guid processId &&
            !await _processOwnershipLookup.ExistsInOrganizationAsync(
                processId,
                actor.OrganizationId,
                cancellationToken))
        {
            return CreateCalendarEventResult.RelatedProcessUnavailable;
        }

        var request = new CalendarEventCreationPersistenceRequest(
            actor.UserId,
            actor.OrganizationId,
            actor.MembershipId,
            command.ClientId,
            command.ProcessId,
            command.AssigneeMembershipId);

        CalendarEventCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                lockedState => DecideCreation(command, request, lockedState),
                cancellationToken);

        return persistenceResult.Status switch
        {
            CalendarEventCreationDecisionStatus.AccessDenied =>
                CreateCalendarEventResult.AccessDenied,
            CalendarEventCreationDecisionStatus.RelatedClientUnavailable =>
                CreateCalendarEventResult.RelatedClientUnavailable,
            CalendarEventCreationDecisionStatus.RelatedProcessUnavailable =>
                CreateCalendarEventResult.RelatedProcessUnavailable,
            CalendarEventCreationDecisionStatus.RelatedAssigneeUnavailable =>
                CreateCalendarEventResult.RelatedAssigneeUnavailable,
            CalendarEventCreationDecisionStatus.InvalidInput =>
                CreateCalendarEventResult.InvalidInput,
            CalendarEventCreationDecisionStatus.Persist
                when persistenceResult.CalendarEventId is Guid calendarEventId =>
                CreateCalendarEventResult.Created(calendarEventId),
            _ => throw new InvalidOperationException(
                "Calendar event creation persistence returned an invalid result.")
        };
    }

    private CalendarEventCreationDecision DecideCreation(
        CreateCalendarEventCommand command,
        CalendarEventCreationPersistenceRequest request,
        CalendarEventCreationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            !IsAvailableActor(state.Actor, request))
        {
            return CalendarEventCreationDecision.AccessDenied;
        }

        CalendarEventMemberState actor = state.Actor!;

        if (!_actionAuthorization.CanCreate(actor.Role) ||
            !_actionAuthorization.CanAssignDuringCreate(
                actor.Role,
                actor.MembershipId,
                command.AssigneeMembershipId))
        {
            return CalendarEventCreationDecision.AccessDenied;
        }

        if (command.AssigneeMembershipId is Guid assigneeMembershipId &&
            !IsAvailableAssignee(
                state.Assignee,
                request.OrganizationId,
                assigneeMembershipId))
        {
            return CalendarEventCreationDecision.RelatedAssigneeUnavailable;
        }

        if (command.ClientId is not null && state.IsClientAvailable != true)
        {
            return CalendarEventCreationDecision.RelatedClientUnavailable;
        }

        if (command.ProcessId is not null && state.IsProcessAvailable != true)
        {
            return CalendarEventCreationDecision.RelatedProcessUnavailable;
        }

        try
        {
            var calendarEvent = new CalendarEvent(
                request.OrganizationId,
                command.Title,
                command.Description,
                command.StartsAt,
                command.EndsAt,
                command.Location,
                command.ClientId,
                command.ProcessId,
                command.AssigneeMembershipId,
                actor.MembershipId,
                _timeProvider.GetUtcNow());

            return CalendarEventCreationDecision.Persist(calendarEvent);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or
                "description" or
                "startsAt" or
                "endsAt" or
                "location" or
                "clientId" or
                "processId" or
                "assigneeMembershipId")
        {
            return CalendarEventCreationDecision.InvalidInput;
        }
    }

    private static bool TryGetAccess(
        CalendarEventAccessAuthorizationResult access,
        CreateCalendarEventCommand command,
        out CalendarEventCreationAccess actor)
    {
        if (access.Status != CalendarEventAccessAuthorizationStatus.Allowed ||
            access.UserId != command.UserId ||
            access.OrganizationId != command.OrganizationId ||
            access.MembershipId is not Guid membershipId ||
            access.Role is not OrganizationRole role)
        {
            actor = null!;
            return false;
        }

        actor = new CalendarEventCreationAccess(
            command.UserId,
            command.OrganizationId,
            membershipId,
            role);
        return true;
    }

    private static bool IsAvailableActor(
        CalendarEventMemberState? actor,
        CalendarEventCreationPersistenceRequest request)
    {
        return actor is not null &&
            actor.MembershipId == request.ActorMembershipId &&
            actor.OrganizationId == request.OrganizationId &&
            actor.UserId == request.UserId &&
            actor.IsMembershipActive &&
            actor.IsUserActive &&
            Enum.IsDefined(actor.Role);
    }

    private static bool IsAvailableAssignee(
        CalendarEventMemberState? assignee,
        Guid organizationId,
        Guid assigneeMembershipId)
    {
        return assignee is not null &&
            assignee.MembershipId == assigneeMembershipId &&
            assignee.OrganizationId == organizationId &&
            assignee.IsMembershipActive &&
            assignee.IsUserActive;
    }

    private static bool HasInvalidInput(CreateCalendarEventCommand command)
    {
        return command.ClientId == Guid.Empty ||
            command.ProcessId == Guid.Empty ||
            command.AssigneeMembershipId == Guid.Empty ||
            command.ClientId is not null && command.ProcessId is not null ||
            command.StartsAt == DateTimeOffset.MinValue ||
            command.EndsAt == DateTimeOffset.MinValue ||
            command.EndsAt <= command.StartsAt;
    }

    private sealed record CalendarEventCreationAccess(
        Guid UserId,
        Guid OrganizationId,
        Guid MembershipId,
        OrganizationRole Role);
}
