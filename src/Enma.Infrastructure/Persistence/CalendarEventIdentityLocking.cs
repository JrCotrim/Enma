using Enma.Application.CalendarEvents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

internal static class CalendarEventIdentityLocking
{
    public static async Task<CalendarEventLockedIdentities> LockAsync(
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

        return new CalendarEventLockedIdentities(
            memberships.ToDictionary(membership => membership.Id),
            users.ToDictionary(user => user.Id));
    }

    public static CalendarEventMemberState? CreateMemberState(
        Guid membershipId,
        CalendarEventLockedIdentities identities)
    {
        if (!identities.MembershipsById.TryGetValue(
                membershipId,
                out OrganizationMembership? membership))
        {
            return null;
        }

        bool isUserActive = identities.UsersById.TryGetValue(
            membership.UserId,
            out User? user) && user.IsActive;

        return new CalendarEventMemberState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            isUserActive);
    }
}

internal sealed record CalendarEventLockedIdentities(
    IReadOnlyDictionary<Guid, OrganizationMembership> MembershipsById,
    IReadOnlyDictionary<Guid, User> UsersById);
