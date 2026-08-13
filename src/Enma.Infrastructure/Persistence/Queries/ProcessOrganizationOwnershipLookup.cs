using Enma.Application.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class ProcessOrganizationOwnershipLookup
    : IProcessOrganizationOwnershipLookup
{
    private readonly EnmaDbContext _dbContext;

    public ProcessOrganizationOwnershipLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> ExistsInOrganizationAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.LegalProcesses
            .AsNoTracking()
            .AnyAsync(
                legalProcess => legalProcess.Id == processId &&
                    legalProcess.OrganizationId == organizationId,
                cancellationToken);
    }
}
