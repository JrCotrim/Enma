using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.CalendarEvents;

internal static class CalendarEventUseCaseSupport
{
    public static async Task<CalendarEventAccess?> GetAccessAsync(
        CalendarEventAccessAuthorization accessAuthorization,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        CalendarEventAccessAuthorizationResult access =
            await accessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (access.Status != CalendarEventAccessAuthorizationStatus.Allowed ||
            access.UserId != userId ||
            access.OrganizationId != organizationId ||
            access.MembershipId is not Guid membershipId ||
            access.Role is not OrganizationRole role)
        {
            return null;
        }

        return new CalendarEventAccess(
            userId,
            organizationId,
            membershipId,
            role);
    }

    public static bool IsAvailableActor(
        CalendarEventMutationLockedState state,
        CalendarEventMutationPersistenceRequest request)
    {
        CalendarEventMemberState? actor = state.Actor;

        return state.IsOrganizationActive &&
            actor is not null &&
            actor.MembershipId == request.ActorMembershipId &&
            actor.OrganizationId == request.OrganizationId &&
            actor.UserId == request.UserId &&
            actor.IsMembershipActive &&
            actor.IsUserActive &&
            Enum.IsDefined(actor.Role);
    }

    public static bool IsAvailableAssignee(
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
}

internal sealed record CalendarEventAccess(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    OrganizationRole Role);
