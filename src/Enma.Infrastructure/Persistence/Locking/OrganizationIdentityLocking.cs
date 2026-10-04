using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Locking;

internal static class OrganizationIdentityLocking
{
    public static async Task<OrganizationLockedIdentities> LockAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        IEnumerable<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        Guid[] orderedMembershipIds = membershipIds
            .Distinct()
            .OrderBy(membershipId => membershipId)
            .ToArray();

        List<OrganizationMembership> memberships =
            await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = ANY ({orderedMembershipIds})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

        Guid[] orderedUserIds = memberships
            .Select(membership => membership.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToArray();
        List<User> users = orderedUserIds.Length == 0
            ? []
            : await dbContext.Users
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM users
                    WHERE id = ANY ({orderedUserIds})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

        return new OrganizationLockedIdentities(
            memberships.ToDictionary(membership => membership.Id),
            users.ToDictionary(user => user.Id));
    }
}

internal sealed record OrganizationLockedIdentities(
    IReadOnlyDictionary<Guid, OrganizationMembership> MembershipsById,
    IReadOnlyDictionary<Guid, User> UsersById);
