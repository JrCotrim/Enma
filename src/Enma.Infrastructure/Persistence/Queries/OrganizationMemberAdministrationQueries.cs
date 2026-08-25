using Enma.Application.Organizations.Members.List;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class OrganizationMemberAdministrationQueries
    : IOrganizationMemberAdministrationQueries
{
    private const string LikeEscapeCharacter = "\\";

    private readonly EnmaDbContext _dbContext;

    public OrganizationMemberAdministrationQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<OrganizationMemberAdministrationPage> ListAsync(
        OrganizationMemberAdministrationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        int skippedItems = checked((query.PageNumber - 1) * query.PageSize);
        bool isActiveMembership = query.MembershipStatus switch
        {
            OrganizationMembershipStatus.Active => true,
            OrganizationMembershipStatus.Inactive => false,
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        bool includeAdministrativeDetails = query.DetailLevel switch
        {
            OrganizationMemberDetailLevel.Basic => false,
            OrganizationMemberDetailLevel.Administrative => true,
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };

        var members =
            from membership in _dbContext.OrganizationMemberships.AsNoTracking()
            join user in _dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == query.OrganizationId &&
                membership.IsActive == isActiveMembership
            select new
            {
                membership.Id,
                user.Name,
                user.Email,
                membership.Role,
                MembershipIsActive = membership.IsActive,
                AccountIsActive = user.IsActive
            };

        if (!includeAdministrativeDetails)
        {
            members = members.Where(member => member.AccountIsActive);
        }

        if (query.Search is not null)
        {
            string pattern = $"%{EscapeLikePattern(query.Search)}%";
            members = includeAdministrativeDetails
                ? members.Where(member =>
                    EF.Functions.ILike(
                        member.Name,
                        pattern,
                        LikeEscapeCharacter) ||
                    EF.Functions.ILike(
                        member.Email,
                        pattern,
                        LikeEscapeCharacter))
                : members.Where(member => EF.Functions.ILike(
                    member.Name,
                    pattern,
                    LikeEscapeCharacter));
        }

        int totalCount = await members.CountAsync(cancellationToken);
        OrganizationMemberAdministrationReadModel[] items =
            includeAdministrativeDetails
                ? await members
                    .OrderBy(member => member.Name)
                    .ThenBy(member => member.Id)
                    .Skip(skippedItems)
                    .Take(query.PageSize)
                    .Select(member => new OrganizationMemberAdministrationReadModel(
                        member.Id,
                        member.Name,
                        member.Email,
                        member.Role,
                        member.MembershipIsActive
                            ? OrganizationMembershipStatus.Active
                            : OrganizationMembershipStatus.Inactive,
                        member.AccountIsActive
                            ? OrganizationAccountStatus.Active
                            : OrganizationAccountStatus.Inactive))
                    .ToArrayAsync(cancellationToken)
                : await members
                    .OrderBy(member => member.Name)
                    .ThenBy(member => member.Id)
                    .Skip(skippedItems)
                    .Take(query.PageSize)
                    .Select(member => new OrganizationMemberAdministrationReadModel(
                        member.Id,
                        member.Name,
                        null,
                        member.Role,
                        null,
                        null))
                    .ToArrayAsync(cancellationToken);

        return new OrganizationMemberAdministrationPage(items, totalCount);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
