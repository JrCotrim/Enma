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

        OrganizationRole? role = await _accessLookup.FindActiveRoleAsync(
            userId,
            organizationId,
            cancellationToken);

        return role.HasValue
            ? OrganizationAccessAuthorizationResult.Allowed(role.Value)
            : OrganizationAccessAuthorizationResult.Denied;
    }
}
