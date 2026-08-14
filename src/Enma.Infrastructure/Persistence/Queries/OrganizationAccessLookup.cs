using Enma.Application.Authorization;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class OrganizationAccessLookup : IOrganizationAccessLookup
{
    private readonly EnmaDbContext _dbContext;

    public OrganizationAccessLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<OrganizationRole?> FindActiveRoleAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<OrganizationRole?> query =
            from membership in _dbContext.OrganizationMemberships
            join organization in _dbContext.Organizations
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.IsActive
                && organization.IsActive
            select (OrganizationRole?)membership.Role;

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<OrganizationAccessLookupResult> query =
            from membership in _dbContext.OrganizationMemberships
            join organization in _dbContext.Organizations
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.IsActive
                && organization.IsActive
            select new OrganizationAccessLookupResult(
                membership.UserId,
                membership.OrganizationId,
                membership.Id,
                membership.Role);

        return query.SingleOrDefaultAsync(cancellationToken);
    }
}
