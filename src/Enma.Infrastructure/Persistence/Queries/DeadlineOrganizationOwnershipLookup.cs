using Enma.Application.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class DeadlineOrganizationOwnershipLookup
    : IDeadlineOrganizationOwnershipLookup
{
    private readonly EnmaDbContext _dbContext;

    public DeadlineOrganizationOwnershipLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> ExistsInOrganizationAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (deadlineId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return _dbContext.LegalDeadlines
            .AsNoTracking()
            .AnyAsync(
                legalDeadline => legalDeadline.Id == deadlineId &&
                    legalDeadline.OrganizationId == organizationId,
                cancellationToken);
    }
}
