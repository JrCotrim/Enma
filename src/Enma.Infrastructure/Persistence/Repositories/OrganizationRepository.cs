using Enma.Application.Organizations;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class OrganizationRepository : IOrganizationRepository
{
    private readonly EnmaDbContext _dbContext;

    public OrganizationRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<Organization?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                organization => organization.Id == id,
                cancellationToken);
    }

    public Task<bool> ExistsBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(
                organization => organization.Slug == slug,
                cancellationToken);
    }

    public async Task AddAsync(
        Organization organization,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Organizations.AddAsync(organization, cancellationToken);
    }
}
