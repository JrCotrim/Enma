using System.Data;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationMemberLifecycleMutationPersistence
    : IOrganizationMemberLifecycleMutationPersistence
{
    private const string LockNotAvailableSqlState = "55P03";

    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public OrganizationMemberLifecycleMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<OrganizationMemberLifecycleMutationPersistenceResult>
        ExecuteAsync(
            OrganizationMemberLifecycleMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.TargetMembershipId == Guid.Empty ||
            !Enum.IsDefined(request.Operation))
        {
            return OrganizationMemberLifecycleMutationPersistenceResult.InvalidInput;
        }

        while (true)
        {
            try
            {
                return await ExecuteAttemptAsync(
                    request,
                    cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForMembershipLocksAsync(request, cancellationToken);
            }
        }
    }

    private async Task<OrganizationMemberLifecycleMutationPersistenceResult>
        ExecuteAttemptAsync(
            OrganizationMemberLifecycleMutationPersistenceRequest request,
            CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        IReadOnlyDictionary<Guid, OrganizationMembership> memberships =
            await LockMembershipsNowaitAsync(
                dbContext,
                request,
                cancellationToken);
        IReadOnlyDictionary<Guid, User> users = await LockUsersAsync(
            dbContext,
            memberships.Values,
            cancellationToken);

        if (organization?.IsActive != true ||
            !memberships.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership) ||
            actorMembership.OrganizationId != request.OrganizationId ||
            actorMembership.UserId != request.UserId ||
            !actorMembership.IsActive ||
            actorMembership.Role is not (
                OrganizationRole.Owner or OrganizationRole.Administrator) ||
            !users.TryGetValue(actorMembership.UserId, out User? actorUser) ||
            !actorUser.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied;
        }

        if (!memberships.TryGetValue(
                request.TargetMembershipId,
                out OrganizationMembership? targetMembership))
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberLifecycleMutationPersistenceResult.NotFound;
        }

        if (targetMembership.Id == actorMembership.Id ||
            targetMembership.Role is not (
                OrganizationRole.Member or OrganizationRole.Administrator) ||
            actorMembership.Role == OrganizationRole.Administrator &&
            targetMembership.Role != OrganizationRole.Member)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied;
        }

        users.TryGetValue(targetMembership.UserId, out User? targetUser);

        if (request.Operation == OrganizationMemberLifecycleOperation.Reactivate)
        {
            if (targetUser?.IsActive != true)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OrganizationMemberLifecycleMutationPersistenceResult
                    .InactiveUserConflict;
            }

            if (targetMembership.IsActive)
            {
                await transaction.CommitAsync(cancellationToken);
                return OrganizationMemberLifecycleMutationPersistenceResult.Succeeded;
            }

            targetMembership.Activate();
        }
        else
        {
            if (!targetMembership.IsActive)
            {
                await transaction.CommitAsync(cancellationToken);
                return OrganizationMemberLifecycleMutationPersistenceResult.Succeeded;
            }

            if (await HasActiveAssignmentsAsync(
                    dbContext,
                    request.OrganizationId,
                    targetMembership.Id,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict;
            }

            targetMembership.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrganizationMemberLifecycleMutationPersistenceResult.Succeeded;
    }

    private async Task WaitForMembershipLocksAsync(
        OrganizationMemberLifecycleMutationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await LockMembershipsAsync(
            dbContext,
            request,
            nowait: false,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
    }

    private static async Task<Organization?> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.Organizations
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organizations
                    WHERE id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static async Task<IReadOnlyDictionary<Guid, OrganizationMembership>>
        LockMembershipsNowaitAsync(
            EnmaDbContext dbContext,
            OrganizationMemberLifecycleMutationPersistenceRequest request,
            CancellationToken cancellationToken)
    {
        return (await LockMembershipsAsync(
                dbContext,
                request,
                nowait: true,
                cancellationToken))
            .ToDictionary(membership => membership.Id);
    }

    private static Task<List<OrganizationMembership>> LockMembershipsAsync(
        EnmaDbContext dbContext,
        OrganizationMemberLifecycleMutationPersistenceRequest request,
        bool nowait,
        CancellationToken cancellationToken)
    {
        Guid[] orderedMembershipIds =
        [
            request.ActorMembershipId,
            request.TargetMembershipId
        ];
        orderedMembershipIds = orderedMembershipIds
            .Distinct()
            .OrderBy(membershipId => membershipId)
            .ToArray();

        if (nowait)
        {
            return dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {request.OrganizationId}
                      AND id = ANY ({orderedMembershipIds})
                    ORDER BY id
                    FOR UPDATE NOWAIT
                    """)
                .ToListAsync(cancellationToken);
        }

        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {request.OrganizationId}
                  AND id = ANY ({orderedMembershipIds})
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<Guid, User>> LockUsersAsync(
        EnmaDbContext dbContext,
        IEnumerable<OrganizationMembership> memberships,
        CancellationToken cancellationToken)
    {
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

        return users.ToDictionary(user => user.Id);
    }

    private static async Task<bool> HasActiveAssignmentsAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid targetMembershipId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        bool hasPendingTask = await dbContext.LegalTasks
            .AsNoTracking()
            .AnyAsync(
                legalTask => legalTask.OrganizationId == organizationId &&
                    legalTask.AssigneeMembershipId == targetMembershipId &&
                    legalTask.CompletedAt == null,
                cancellationToken);

        return hasPendingTask || await dbContext.CalendarEvents
            .AsNoTracking()
            .AnyAsync(
                calendarEvent =>
                    calendarEvent.OrganizationId == organizationId &&
                    calendarEvent.AssigneeMembershipId == targetMembershipId &&
                    calendarEvent.EndsAt > nowUtc,
                cancellationToken);
    }

    private static bool IsLockNotAvailable(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException postgresException &&
                postgresException.SqlState == LockNotAvailableSqlState)
            {
                return true;
            }
        }

        return false;
    }
}
