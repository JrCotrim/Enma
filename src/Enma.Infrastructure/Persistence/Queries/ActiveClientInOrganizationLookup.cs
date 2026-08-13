using Enma.Application.Processes;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class ActiveClientInOrganizationLookup
    : IActiveClientInOrganizationLookup
{
    private readonly EnmaDbContext _dbContext;

    public ActiveClientInOrganizationLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> ExistsAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (clientId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return _dbContext.Clients
            .AsNoTracking()
            .AnyAsync(
                client => client.Id == clientId &&
                    client.OrganizationId == organizationId &&
                    client.IsActive,
                cancellationToken);
    }
}
