using Enma.Domain.Organizations;

namespace Enma.Application.Organizations;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Organization organization,
        CancellationToken cancellationToken = default);
}
