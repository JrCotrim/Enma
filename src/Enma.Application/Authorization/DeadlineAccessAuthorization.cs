namespace Enma.Application.Authorization;

public sealed class DeadlineAccessAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly IDeadlineOrganizationOwnershipLookup _ownershipLookup;

    public DeadlineAccessAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IDeadlineOrganizationOwnershipLookup ownershipLookup)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(ownershipLookup);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _ownershipLookup = ownershipLookup;
    }

    public async Task<DeadlineAccessAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            deadlineId == Guid.Empty)
        {
            return DeadlineAccessAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess =
            await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied)
        {
            return DeadlineAccessAuthorizationResult.Denied;
        }

        bool existsInOrganization = await _ownershipLookup.ExistsInOrganizationAsync(
            deadlineId,
            organizationId,
            cancellationToken);

        return existsInOrganization
            ? DeadlineAccessAuthorizationResult.Allowed
            : DeadlineAccessAuthorizationResult.Denied;
    }
}
