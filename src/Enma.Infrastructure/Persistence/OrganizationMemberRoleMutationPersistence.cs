using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Organizations.Members.Role;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationMemberRoleMutationPersistence
    : IOrganizationMemberRoleMutationPersistence
{
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
            await LockMembershipsAsync(dbContext, request, cancellationToken);
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
        LockMembershipsAsync(
            EnmaDbContext dbContext,
            OrganizationMemberRoleMutationPersistenceRequest request,
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

        List<OrganizationMembership> memberships =
            await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {request.OrganizationId}
                      AND id = ANY ({orderedMembershipIds})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

        return memberships.ToDictionary(membership => membership.Id);
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
}
