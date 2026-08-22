using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class CalendarEventAccessAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public CalendarEventAccessAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<CalendarEventAccessAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult organizationAccess;

        try
        {
            organizationAccess = await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return CalendarEventAccessAuthorizationResult.Denied;
        }

        if (organizationAccess.Status != OrganizationAccessAuthorizationStatus.Allowed ||
            organizationAccess.UserId != userId ||
            organizationAccess.OrganizationId != organizationId ||
            organizationAccess.MembershipId is not Guid membershipId ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return CalendarEventAccessAuthorizationResult.Denied;
        }

        return role switch
        {
            OrganizationRole.Owner or
            OrganizationRole.Administrator or
            OrganizationRole.Member =>
                CalendarEventAccessAuthorizationResult.Allowed(
                    userId,
                    organizationId,
                    membershipId,
                    role),
            _ => CalendarEventAccessAuthorizationResult.Denied
        };
    }
}
