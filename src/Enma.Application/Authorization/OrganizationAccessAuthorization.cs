using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAccessAuthorization
{
    private readonly IOrganizationAccessLookup _accessLookup;

    public OrganizationAccessAuthorization(IOrganizationAccessLookup accessLookup)
    {
        ArgumentNullException.ThrowIfNull(accessLookup);
        _accessLookup = accessLookup;
    }

    public async Task<OrganizationAccessAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || organizationId == Guid.Empty)
        {
            return OrganizationAccessAuthorizationResult.Denied;
        }

        OrganizationAccessLookupResult? access =
            await _accessLookup.FindActiveAccessAsync(
                userId,
                organizationId,
                cancellationToken);

        return access is not null
            ? OrganizationAccessAuthorizationResult.Allowed(
                access.UserId,
                access.OrganizationId,
                access.MembershipId,
                access.Role)
            : OrganizationAccessAuthorizationResult.Denied;
    }
}
