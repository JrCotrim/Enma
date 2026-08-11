using Enma.Application.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class ClientOrganizationOwnershipLookup
    : IClientOrganizationOwnershipLookup
{
    private readonly EnmaDbContext _dbContext;

    public ClientOrganizationOwnershipLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> ExistsInOrganizationAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Clients
            .AsNoTracking()
            .AnyAsync(
                client => client.Id == clientId &&
                    client.OrganizationId == organizationId,
                cancellationToken);
    }
}
