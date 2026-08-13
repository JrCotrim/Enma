namespace Enma.Application.Authorization;

public sealed class ProcessAccessAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly IProcessOrganizationOwnershipLookup _ownershipLookup;

    public ProcessAccessAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IProcessOrganizationOwnershipLookup ownershipLookup)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(ownershipLookup);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _ownershipLookup = ownershipLookup;
    }

    public async Task<ProcessAccessAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            processId == Guid.Empty)
        {
            return ProcessAccessAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess =
            await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied)
        {
            return ProcessAccessAuthorizationResult.Denied;
        }

        bool existsInOrganization = await _ownershipLookup.ExistsInOrganizationAsync(
            processId,
            organizationId,
            cancellationToken);

        return existsInOrganization
            ? ProcessAccessAuthorizationResult.Allowed
            : ProcessAccessAuthorizationResult.Denied;
    }
}
