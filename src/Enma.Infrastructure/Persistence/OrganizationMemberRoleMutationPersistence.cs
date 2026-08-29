using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Organizations.Members.Role;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationMemberRoleMutationPersistence
    : IOrganizationMemberRoleMutationPersistence
{
    private const string LockNotAvailableSqlState = "55P03";

    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public OrganizationMemberRoleMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<OrganizationMemberRoleMutationPersistenceResult> ExecuteAsync(
        OrganizationMemberRoleMutationPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsMutableRole(request.Role) ||
            !IsMutableRole(request.ExpectedCurrentRole))
        {
            return OrganizationMemberRoleMutationPersistenceResult.InvalidInput;
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

    private async Task<OrganizationMemberRoleMutationPersistenceResult>
        ExecuteAttemptAsync(
            OrganizationMemberRoleMutationPersistenceRequest request,
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
        User? actorUser = await LockActorUserAsync(
            dbContext,
            request.UserId,
            cancellationToken);

        if (organization is null ||
            !organization.IsActive ||
            actorUser is null ||
            !actorUser.IsActive ||
            !memberships.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership) ||
            actorMembership.OrganizationId != request.OrganizationId ||
            actorMembership.UserId != request.UserId ||
            !actorMembership.IsActive ||
            actorMembership.Role != OrganizationRole.Owner)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.AccessDenied;
        }

        if (!memberships.TryGetValue(
                request.TargetMembershipId,
                out OrganizationMembership? targetMembership))
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.NotFound;
        }

        if (targetMembership.Role == OrganizationRole.Owner)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.TargetForbidden;
        }

        if (!targetMembership.IsActive ||
            !IsMutableRole(targetMembership.Role))
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.Conflict;
        }

        if (targetMembership.Role == request.Role)
        {
            await transaction.CommitAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.Succeeded;
        }

        if (targetMembership.Role != request.ExpectedCurrentRole ||
            request.Role == request.ExpectedCurrentRole)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationMemberRoleMutationPersistenceResult.Conflict;
        }

        OrganizationRole oldRole = targetMembership.Role;
        targetMembership.ChangeRole(request.Role);
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.OrganizationMembershipRoleChanged,
                targetMembership.Id,
                new OrganizationMembershipRoleChangedAuditDetails(
                    oldRole,
                    targetMembership.Role)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrganizationMemberRoleMutationPersistenceResult.Succeeded;
    }

    private async Task WaitForMembershipLocksAsync(
        OrganizationMemberRoleMutationPersistenceRequest request,
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
            OrganizationMemberRoleMutationPersistenceRequest request,
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
        OrganizationMemberRoleMutationPersistenceRequest request,
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

    private static async Task<User?> LockActorUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.Users
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM users
                    WHERE id = {userId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static bool IsMutableRole(OrganizationRole role)
    {
        return role is OrganizationRole.Administrator or OrganizationRole.Member;
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
