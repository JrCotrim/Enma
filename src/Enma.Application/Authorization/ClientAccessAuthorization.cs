namespace Enma.Application.Authorization;

public sealed class ClientAccessAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly IClientOrganizationOwnershipLookup _ownershipLookup;

    public ClientAccessAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IClientOrganizationOwnershipLookup ownershipLookup)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(ownershipLookup);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _ownershipLookup = ownershipLookup;
    }

    public async Task<ClientAccessAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            clientId == Guid.Empty)
        {
            return ClientAccessAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess =
            await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied)
        {
            return ClientAccessAuthorizationResult.Denied;
        }

        bool existsInOrganization = await _ownershipLookup.ExistsInOrganizationAsync(
            clientId,
            organizationId,
            cancellationToken);

        return existsInOrganization
            ? ClientAccessAuthorizationResult.Allowed
            : ClientAccessAuthorizationResult.Denied;
    }
}
