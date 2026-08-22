using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class CalendarEventActionAuthorization
{
    public bool CanView(OrganizationRole role)
    {
        return IsCalendarRole(role);
    }

    public bool CanCreate(OrganizationRole role)
    {
        return IsCalendarRole(role);
    }

    public bool CanAssignDuringCreate(
        OrganizationRole role,
        Guid actorMembershipId,
        Guid? requestedAssigneeMembershipId)
    {
        return IsRequestedAssigneeAllowed(
            role,
            actorMembershipId,
            requestedAssigneeMembershipId);
    }

    public bool CanRequestAssigneeChange(
        OrganizationRole role,
        Guid actorMembershipId,
        Guid? requestedAssigneeMembershipId)
    {
        return IsRequestedAssigneeAllowed(
            role,
            actorMembershipId,
            requestedAssigneeMembershipId);
    }

    public bool CanUpdate(
        OrganizationRole role,
        Guid actorMembershipId,
        CalendarEventAuthorizationState calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return role switch
        {
            OrganizationRole.Owner or OrganizationRole.Administrator => true,
            OrganizationRole.Member => IsCreator(
                actorMembershipId,
                calendarEvent),
            _ => false
        };
    }

    public bool CanChangeAssignee(
        OrganizationRole role,
        Guid actorMembershipId,
        CalendarEventAuthorizationState calendarEvent,
        Guid? requestedAssigneeMembershipId)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return CanUpdate(role, actorMembershipId, calendarEvent) &&
            IsRequestedAssigneeAllowed(
                role,
                actorMembershipId,
                requestedAssigneeMembershipId);
    }

    public bool CanDelete(
        OrganizationRole role,
        Guid actorMembershipId,
        CalendarEventAuthorizationState calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return role switch
        {
            OrganizationRole.Owner or OrganizationRole.Administrator => true,
            OrganizationRole.Member => IsCreator(
                actorMembershipId,
                calendarEvent),
            _ => false
        };
    }

    private static bool IsCalendarRole(OrganizationRole role)
    {
        return role is OrganizationRole.Owner or
            OrganizationRole.Administrator or
            OrganizationRole.Member;
    }

    private static bool IsRequestedAssigneeAllowed(
        OrganizationRole role,
        Guid actorMembershipId,
        Guid? requestedAssigneeMembershipId)
    {
        return role switch
        {
            OrganizationRole.Owner or OrganizationRole.Administrator => true,
            OrganizationRole.Member =>
                actorMembershipId != Guid.Empty &&
                (requestedAssigneeMembershipId is null ||
                    requestedAssigneeMembershipId == actorMembershipId),
            _ => false
        };
    }

    private static bool IsCreator(
        Guid actorMembershipId,
        CalendarEventAuthorizationState calendarEvent)
    {
        return actorMembershipId != Guid.Empty &&
            calendarEvent.CreatedByMembershipId != Guid.Empty &&
            calendarEvent.CreatedByMembershipId == actorMembershipId;
    }
}

public sealed record CalendarEventAuthorizationState(
    Guid CreatedByMembershipId);
