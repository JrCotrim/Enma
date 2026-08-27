using System.Data;
using Enma.Application.Organizations.UpdateName;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationNameMutationPersistence
    : IOrganizationNameMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public OrganizationNameMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
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

        organization.Rename(request.Name);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return OrganizationNameMutationPersistenceResult.Succeeded;
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
        CancellationToken cancellationToken)
    {
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
}
