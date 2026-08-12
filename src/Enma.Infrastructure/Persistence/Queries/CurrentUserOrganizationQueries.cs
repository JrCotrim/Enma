using Enma.Application.Organizations.CurrentUser;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class CurrentUserOrganizationQueries
    : ICurrentUserOrganizationQueries
{
    private readonly EnmaDbContext _dbContext;

    public CurrentUserOrganizationQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CurrentUserOrganizationReadModel>>
        ListAccessibleAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
    {
        IQueryable<CurrentUserOrganizationReadModel> query =
            from membership in _dbContext.OrganizationMemberships.AsNoTracking()
            join organization in _dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
                && membership.IsActive
                && organization.IsActive
            orderby organization.Name, organization.Id
            select new CurrentUserOrganizationReadModel(
                organization.Id,
                organization.Name,
                membership.Role);

        return await query.ToArrayAsync(cancellationToken);
    }
}
