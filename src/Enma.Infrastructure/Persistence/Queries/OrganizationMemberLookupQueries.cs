using Enma.Application.Organizations.Members.Lookup;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class OrganizationMemberLookupQueries : IOrganizationMemberLookupQueries
{
    private const string LikeEscapeCharacter = "\\";

    private readonly EnmaDbContext _dbContext;

    public OrganizationMemberLookupQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<OrganizationMemberLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int skippedItems = checked((pageNumber - 1) * pageSize);
        var query =
            from membership in _dbContext.OrganizationMemberships.AsNoTracking()
            join user in _dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
                && membership.IsActive
                && user.IsActive
            select new
            {
                membership.Id,
                DisplayName = user.Name
            };

        if (search is not null)
        {
            string pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(member => EF.Functions.ILike(
                member.DisplayName,
                pattern,
                LikeEscapeCharacter));
        }

        return await query
            .OrderBy(member => member.DisplayName)
            .ThenBy(member => member.Id)
            .Skip(skippedItems)
            .Take(pageSize + 1)
            .Select(member => new OrganizationMemberLookupItem(
                member.Id,
                member.DisplayName))
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
