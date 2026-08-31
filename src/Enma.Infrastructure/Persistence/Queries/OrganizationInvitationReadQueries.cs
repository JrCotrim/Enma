using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class OrganizationInvitationReadQueries
    : IOrganizationInvitationReadQueries
{
    private readonly EnmaDbContext dbContext;

    public OrganizationInvitationReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        this.dbContext = dbContext;
    }

    public async Task<OrganizationInvitationPage> ListAsync(
        OrganizationInvitationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        int skippedItems = checked((query.PageNumber - 1) * query.PageSize);
        IQueryable<OrganizationInvitation> invitations = dbContext
            .OrganizationInvitations
            .AsNoTracking()
            .Where(invitation =>
                invitation.OrganizationId == query.OrganizationId);

        int totalCount = await invitations.CountAsync(cancellationToken);
        OrganizationInvitationReadModel[] items = await invitations
            .OrderByDescending(invitation => invitation.CreatedAt)
            .ThenByDescending(invitation => invitation.Id)
            .Skip(skippedItems)
            .Take(query.PageSize)
            .Select(invitation => new OrganizationInvitationReadModel(
                invitation.Id,
                invitation.InvitedEmail,
                invitation.Role,
                invitation.AcceptedAt != null
                    ? OrganizationInvitationState.Accepted
                    : invitation.RevokedAt != null
                        ? OrganizationInvitationState.Revoked
                        : invitation.ExpiredAt != null ||
                            invitation.ExpiresAt <= query.Now
                            ? OrganizationInvitationState.Expired
                            : OrganizationInvitationState.Pending,
                invitation.CreatedAt,
                invitation.ExpiresAt,
                invitation.CreatedByMembershipId))
            .ToArrayAsync(cancellationToken);

        return new OrganizationInvitationPage(items, totalCount);
    }

    public Task<OrganizationRole?> FindRoleAsync(
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.OrganizationInvitations
            .AsNoTracking()
            .Where(invitation =>
                invitation.OrganizationId == organizationId &&
                invitation.Id == invitationId)
            .Select(invitation => (OrganizationRole?)invitation.Role)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
