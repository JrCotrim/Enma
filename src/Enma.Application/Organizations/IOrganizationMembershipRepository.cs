using Enma.Domain.Organizations;

namespace Enma.Application.Organizations;

public interface IOrganizationMembershipRepository
{
    Task AddAsync(
        OrganizationMembership membership,
        CancellationToken cancellationToken = default);
}
