using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public interface IOrganizationAccessLookup
{
    Task<OrganizationRole?> FindActiveRoleAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    async Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        OrganizationRole? role = await FindActiveRoleAsync(
            userId,
            organizationId,
            cancellationToken);

        return role.HasValue
            ? new OrganizationAccessLookupResult(
                userId,
                organizationId,
                null,
                role.Value)
            : null;
    }
}

public sealed record OrganizationAccessLookupResult(
    Guid UserId,
    Guid OrganizationId,
    Guid? MembershipId,
    OrganizationRole Role);
