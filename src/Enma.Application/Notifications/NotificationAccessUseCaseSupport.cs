using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Notifications;

internal static class NotificationAccessUseCaseSupport
{
    public static async Task<bool> HasAccessAsync(
        OrganizationAccessAuthorization accessAuthorization,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await accessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return false;
        }

        return access.Status == OrganizationAccessAuthorizationStatus.Allowed &&
            access.UserId == userId &&
            access.OrganizationId == organizationId &&
            access.MembershipId is Guid &&
            access.Role is OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member;
    }
}
