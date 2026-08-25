using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAdministrationAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public OrganizationAdministrationAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<OrganizationAdministrationAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || organizationId == Guid.Empty)
        {
            return OrganizationAdministrationAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "userId" or
                "organizationId" or
                "membershipId" or
                "role")
        {
            return OrganizationAdministrationAuthorizationResult.Denied;
        }

        if (access.Status != OrganizationAccessAuthorizationStatus.Allowed ||
            access.UserId != userId ||
            access.OrganizationId != organizationId ||
            access.MembershipId is not Guid membershipId ||
            membershipId == Guid.Empty ||
            access.Role is not OrganizationRole role ||
            !Enum.IsDefined(role))
        {
            return OrganizationAdministrationAuthorizationResult.Denied;
        }

        return OrganizationAdministrationAuthorizationResult.Allowed(role);
    }
}
