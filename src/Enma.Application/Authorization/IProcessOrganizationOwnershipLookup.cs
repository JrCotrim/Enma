namespace Enma.Application.Authorization;

public interface IProcessOrganizationOwnershipLookup
{
    Task<bool> ExistsInOrganizationAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
