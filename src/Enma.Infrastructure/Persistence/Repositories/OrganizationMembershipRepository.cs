using Enma.Application.Organizations;
using Enma.Domain.Organizations;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class OrganizationMembershipRepository
    : IOrganizationMembershipRepository
{
    private readonly EnmaDbContext _dbContext;

    public OrganizationMembershipRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task AddAsync(
        OrganizationMembership membership,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.OrganizationMemberships.AddAsync(
            membership,
            cancellationToken);
    }
}
