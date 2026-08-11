namespace Enma.Application.Authorization;

public interface IClientOrganizationOwnershipLookup
{
    Task<bool> ExistsInOrganizationAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
