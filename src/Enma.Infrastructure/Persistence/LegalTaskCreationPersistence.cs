using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Tasks;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalTaskCreationPersistence : ILegalTaskCreationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalTaskCreationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
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

        LegalProcess? process = request.ProcessId is Guid processId
            ? await LockProcessAsync(
                dbContext,
                request.OrganizationId,
                processId,
                cancellationToken)
            : null;

        if (request.ProcessId is not null && process is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalTaskCreationPersistenceResult.Rejected(
                LegalTaskCreationDecisionStatus.RelatedProcessUnavailable);
        }

        LegalTaskLockedIdentities identities = await LegalTaskIdentityLocking.LockAsync(
            dbContext,
            request.OrganizationId,
            GetMembershipIds(request),
            cancellationToken);
        IReadOnlyDictionary<Guid, LegalTaskCreationMemberState> statesById =
            identities.MembershipsById.Values.ToDictionary(
                membership => membership.Id,
                membership => CreateMemberState(
                    membership,
                    identities.UsersById));

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
        OrganizationMembership actorMembership =
            identities.MembershipsById[request.ActorMembershipId];
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(AuditEventType.LegalTaskCreated, legalTask.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalTaskCreationPersistenceResult.Succeeded(legalTask.Id);
    }

    private static Task<LegalProcess?> LockProcessAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        return dbContext.LegalProcesses
            .FromSqlInterpolated(
                $"""
                SELECT * FROM legal_processes
                WHERE organization_id = {organizationId}
                  AND id = {processId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static IEnumerable<Guid> GetMembershipIds(
        LegalTaskCreationPersistenceRequest request)
    {
        return request.AssigneeMembershipId is Guid assigneeMembershipId
            ? new[] { request.ActorMembershipId, assigneeMembershipId }
                .Distinct()
                .OrderBy(membershipId => membershipId)
                .ToArray()
            : [request.ActorMembershipId];
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
