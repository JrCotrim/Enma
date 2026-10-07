using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Organizations.Members.Ownership;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationOwnershipTransferPersistence
    : IOrganizationOwnershipTransferPersistence
{
    private const string LockNotAvailableSqlState = "55P03";

    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public OrganizationOwnershipTransferPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<OrganizationOwnershipTransferPersistenceResult> ExecuteAsync(
        OrganizationOwnershipTransferPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.TargetMembershipId == Guid.Empty ||
            request.ExpectedTargetRole is not (
                OrganizationRole.Administrator or OrganizationRole.Member))
        {
            return OrganizationOwnershipTransferPersistenceResult.InvalidInput;
        }

        while (true)
        {
            try
            {
                return await ExecuteAttemptAsync(request, cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForMembershipLocksAsync(request, cancellationToken);
            }
        }
    }

    private async Task<OrganizationOwnershipTransferPersistenceResult>
        ExecuteAttemptAsync(
            OrganizationOwnershipTransferPersistenceRequest request,
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
            actorMembership.Role != OrganizationRole.Owner ||
            !users.TryGetValue(actorMembership.UserId, out User? actorUser) ||
            !actorUser.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationOwnershipTransferPersistenceResult.AccessDenied;
        }

        if (!memberships.TryGetValue(
                request.TargetMembershipId,
                out OrganizationMembership? targetMembership) ||
            targetMembership.OrganizationId != request.OrganizationId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationOwnershipTransferPersistenceResult.NotFound;
        }

        if (targetMembership.Id == actorMembership.Id ||
            !targetMembership.IsActive ||
            targetMembership.Role != OrganizationRole.Administrator ||
            targetMembership.Role != request.ExpectedTargetRole ||
            !users.TryGetValue(targetMembership.UserId, out User? targetUser) ||
            !targetUser.IsActive ||
            targetUser.EmailVerifiedAt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationOwnershipTransferPersistenceResult.TargetUnavailable;
        }

        // Captured before the demotion so the event records the actor as Owner.
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);

        // Demote first: the single-Owner unique index is checked per statement.
        actorMembership.DemoteOwnerToAdministrator();
        await dbContext.SaveChangesAsync(cancellationToken);

        targetMembership.PromoteAdministratorToOwner();
        await dbContext.SaveChangesAsync(cancellationToken);

        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.OrganizationOwnershipTransferred,
                organization.Id,
                new OrganizationOwnershipTransferredAuditDetails(
                    actorMembership.Id,
                    targetMembership.Id)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrganizationOwnershipTransferPersistenceResult.Succeeded;
    }

    private async Task WaitForMembershipLocksAsync(
        OrganizationOwnershipTransferPersistenceRequest request,
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
            OrganizationOwnershipTransferPersistenceRequest request,
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
        OrganizationOwnershipTransferPersistenceRequest request,
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
