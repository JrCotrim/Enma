using System.Data;
using Enma.Application.Tasks;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalTaskCreationPersistence : ILegalTaskCreationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public LegalTaskCreationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<LegalTaskCreationPersistenceResult> ExecuteAsync(
        LegalTaskCreationPersistenceRequest request,
        Func<LegalTaskCreationLockedState, LegalTaskCreationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Guid[] membershipIds = GetOrderedMembershipIds(request);
        List<OrganizationMembership> memberships =
            await LockMembershipsAsync(
                dbContext,
                request.OrganizationId,
                membershipIds,
                cancellationToken);
        Guid[] userIds = memberships
            .Select(membership => membership.UserId)
            .Distinct()
            .OrderBy(userId => userId)
            .ToArray();
        List<User> users = await LockUsersAsync(
            dbContext,
            userIds,
            cancellationToken);
        IReadOnlyDictionary<Guid, User> usersById = users.ToDictionary(
            user => user.Id);
        IReadOnlyDictionary<Guid, LegalTaskCreationMemberState> statesById =
            memberships.ToDictionary(
                membership => membership.Id,
                membership => CreateMemberState(membership, usersById));

        statesById.TryGetValue(
            request.ActorMembershipId,
            out LegalTaskCreationMemberState? actor);
        LegalTaskCreationMemberState? assignee = null;

        if (request.AssigneeMembershipId is Guid assigneeMembershipId)
        {
            statesById.TryGetValue(assigneeMembershipId, out assignee);
        }

        LegalTaskCreationDecision decision = decide(
            new LegalTaskCreationLockedState(actor, assignee));

        if (decision.Status != LegalTaskCreationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalTaskCreationPersistenceResult.Rejected(decision.Status);
        }

        if (decision.LegalTask is not { } legalTask)
        {
            throw new InvalidOperationException(
                "A persistence decision must include a legal task.");
        }

        await dbContext.LegalTasks.AddAsync(legalTask, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalTaskCreationPersistenceResult.Succeeded(legalTask.Id);
    }

    private static Guid[] GetOrderedMembershipIds(
        LegalTaskCreationPersistenceRequest request)
    {
        return request.AssigneeMembershipId is Guid assigneeMembershipId
            ? new[] { request.ActorMembershipId, assigneeMembershipId }
                .Distinct()
                .OrderBy(membershipId => membershipId)
                .ToArray()
            : [request.ActorMembershipId];
    }

    private static Task<List<OrganizationMembership>> LockMembershipsAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid[] membershipIds,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {organizationId}
                  AND id = ANY ({membershipIds})
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    private static Task<List<User>> LockUsersAsync(
        EnmaDbContext dbContext,
        Guid[] userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Length == 0)
        {
            return Task.FromResult(new List<User>());
        }

        return dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = ANY ({userIds})
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    private static LegalTaskCreationMemberState CreateMemberState(
        OrganizationMembership membership,
        IReadOnlyDictionary<Guid, User> usersById)
    {
        bool isUserActive = usersById.TryGetValue(
            membership.UserId,
            out User? user) && user.IsActive;

        return new LegalTaskCreationMemberState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            isUserActive);
    }
}
