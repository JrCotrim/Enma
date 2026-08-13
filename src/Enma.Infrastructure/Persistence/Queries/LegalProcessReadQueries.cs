using Enma.Application.Processes;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalProcessReadQueries : ILegalProcessReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public LegalProcessReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalProcessReadModel?> FindAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LegalProcessReadModel> query =
            from legalProcess in _dbContext.LegalProcesses.AsNoTracking()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            where legalProcess.Id == processId &&
                legalProcess.OrganizationId == organizationId
            select new LegalProcessReadModel(
                legalProcess.Id,
                legalProcess.Title,
                legalProcess.ClientId,
                client.Name,
                legalProcess.CreatedAt);

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        long skippedItems = ((long)pageNumber - 1) * pageSize;

        if (skippedItems > int.MaxValue)
        {
            return Array.Empty<LegalProcessReadModel>();
        }

        IQueryable<LegalProcessReadModel> query =
            from legalProcess in _dbContext.LegalProcesses.AsNoTracking()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            where legalProcess.OrganizationId == organizationId
            orderby legalProcess.Title, legalProcess.Id
            select new LegalProcessReadModel(
                legalProcess.Id,
                legalProcess.Title,
                legalProcess.ClientId,
                client.Name,
                legalProcess.CreatedAt);

        return await query
            .Skip((int)skippedItems)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
    }
}
