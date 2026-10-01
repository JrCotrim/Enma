using Enma.Application.Processes.Lookup;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalProcessLookupQueries : ILegalProcessLookupQueries
{
    private const string LikeEscapeCharacter =
        LegalProcessSearchPattern.LikeEscapeCharacter;

    private readonly EnmaDbContext _dbContext;

    public LegalProcessLookupQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<LegalProcessLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int skippedItems = checked((pageNumber - 1) * pageSize);
        var query =
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
            select new
            {
                legalProcess.Id,
                legalProcess.Title,
                ClientName = client.Name,
                legalProcess.ProcessNumber,
                legalProcess.NormalizedProcessNumber,
                legalProcess.Status,
                legalProcess.ResponsibleMembershipId
            };

        if (LegalProcessSearchPattern.Create(search) is { } searchPattern)
        {
            string pattern = searchPattern.ContainsPattern;
            string? digitsPattern = searchPattern.DigitsContainsPattern;
            query = query.Where(item =>
                EF.Functions.ILike(
                    item.Title,
                    pattern,
                    LikeEscapeCharacter) ||
                EF.Functions.ILike(
                    item.ClientName,
                    pattern,
                    LikeEscapeCharacter) ||
                (item.ProcessNumber != null &&
                    EF.Functions.ILike(
                        item.ProcessNumber,
                        pattern,
                        LikeEscapeCharacter)) ||
                (digitsPattern != null &&
                    item.NormalizedProcessNumber != null &&
                    EF.Functions.Like(
                        item.NormalizedProcessNumber,
                        digitsPattern,
                        LikeEscapeCharacter)));
        }

        return await query
            .OrderBy(item => item.Title)
            .ThenBy(item => item.Id)
            .Skip(skippedItems)
            .Take(pageSize + 1)
            .Select(item => new LegalProcessLookupItem(
                item.Id,
                item.Title,
                item.ClientName,
                item.ProcessNumber,
                item.Status,
                item.ResponsibleMembershipId))
            .ToArrayAsync(cancellationToken);
    }
}
