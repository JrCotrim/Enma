using Enma.Application.Clients;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class ClientReadQueries : IClientReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public ClientReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<ClientReadModel?> FindAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Clients
            .AsNoTracking()
            .Where(client =>
                client.Id == clientId &&
                client.OrganizationId == organizationId)
            .Select(client => new ClientReadModel(
                client.Id,
                client.Name,
                client.IsActive,
                client.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClientReadModel>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        long skippedItems = ((long)pageNumber - 1) * pageSize;

        if (skippedItems > int.MaxValue)
        {
            return Array.Empty<ClientReadModel>();
        }

        return await _dbContext.Clients
            .AsNoTracking()
            .Where(client => client.OrganizationId == organizationId)
            .OrderBy(client => client.Name)
            .ThenBy(client => client.Id)
            .Skip((int)skippedItems)
            .Take(pageSize)
            .Select(client => new ClientReadModel(
                client.Id,
                client.Name,
                client.IsActive,
                client.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }
}
