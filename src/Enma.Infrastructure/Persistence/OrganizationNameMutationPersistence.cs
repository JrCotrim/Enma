using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Organizations.UpdateName;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationNameMutationPersistence
    : IOrganizationNameMutationPersistence
{
    private const string LockNotAvailableSqlState = "55P03";

    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public OrganizationNameMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<OrganizationNameMutationPersistenceResult> ExecuteAsync(
        OrganizationNameMutationPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty)
        {
            return OrganizationNameMutationPersistenceResult.InvalidInput;
        }

        while (true)
        {
            try
            {
                return await ExecuteAttemptAsync(request, cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForActorMembershipLockAsync(request, cancellationToken);
            }
        }
    }

    private async Task<OrganizationNameMutationPersistenceResult> ExecuteAttemptAsync(
        OrganizationNameMutationPersistenceRequest request,
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
        OrganizationMembership? actorMembership = await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            nowait: true,
            cancellationToken);
        User? actorUser = await LockActorUserAsync(
            dbContext,
            request.UserId,
            cancellationToken);

        if (organization?.IsActive != true ||
            actorMembership is null ||
            actorMembership.OrganizationId != request.OrganizationId ||
            actorMembership.UserId != request.UserId ||
            !actorMembership.IsActive ||
            actorMembership.Role != OrganizationRole.Owner ||
            actorUser?.IsActive != true)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OrganizationNameMutationPersistenceResult.AccessDenied;
        }

        string oldName = organization.Name;
        organization.Rename(request.Name);

        if (organization.Name == oldName)
        {
            await transaction.CommitAsync(cancellationToken);
            return OrganizationNameMutationPersistenceResult.Succeeded;
        }

        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.OrganizationRenamed,
                organization.Id,
                new OrganizationRenamedAuditDetails(oldName, organization.Name)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrganizationNameMutationPersistenceResult.Succeeded;
    }

    private async Task WaitForActorMembershipLockAsync(
        OrganizationNameMutationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
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

    private static async Task<OrganizationMembership?> LockActorMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid actorMembershipId,
        bool nowait,
        CancellationToken cancellationToken)
    {
        if (nowait)
        {
            return (await dbContext.OrganizationMemberships
                    .FromSqlInterpolated(
                        $"""
                        SELECT * FROM organization_memberships
                        WHERE organization_id = {organizationId}
                          AND id = {actorMembershipId}
                        FOR UPDATE NOWAIT
                        """)
                    .ToListAsync(cancellationToken))
                .SingleOrDefault();
        }

        return (await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {actorMembershipId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
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
