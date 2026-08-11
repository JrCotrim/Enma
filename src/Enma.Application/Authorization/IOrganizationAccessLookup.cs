using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public interface IOrganizationAccessLookup
{
    Task<OrganizationRole?> FindActiveRoleAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
