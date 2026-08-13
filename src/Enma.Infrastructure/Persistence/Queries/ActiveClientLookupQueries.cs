using Enma.Application.Clients.Lookup;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class ActiveClientLookupQueries : IActiveClientLookupQueries
{
    private const string LikeEscapeCharacter = "\\";

    private readonly EnmaDbContext _dbContext;

    public ActiveClientLookupQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ActiveClientLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int skippedItems = checked((pageNumber - 1) * pageSize);
        IQueryable<Domain.Clients.Client> query = _dbContext.Clients
            .AsNoTracking()
            .Where(client =>
                client.OrganizationId == organizationId &&
                client.IsActive);

        if (search is not null)
        {
            string pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(client => EF.Functions.ILike(
                client.Name,
                pattern,
                LikeEscapeCharacter));
        }

        return await query
            .OrderBy(client => client.Name)
            .ThenBy(client => client.Id)
            .Skip(skippedItems)
            .Take(pageSize + 1)
            .Select(client => new ActiveClientLookupItem(
                client.Id,
                client.Name))
            .ToArrayAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
