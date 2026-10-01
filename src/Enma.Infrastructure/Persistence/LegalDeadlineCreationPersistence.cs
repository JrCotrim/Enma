using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Deadlines;
using Enma.Domain.Auditing;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDeadlineCreationPersistence
    : ILegalDeadlineCreationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalDeadlineCreationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<LegalDeadlineCreationPersistenceResult> ExecuteAsync(
        LegalDeadlineCreationPersistenceRequest request,
        Func<LegalDeadlineCreationLockedState, LegalDeadlineCreationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.ProcessId == Guid.Empty)
        {
            return LegalDeadlineCreationPersistenceResult.Rejected(
                LegalDeadlineCreationDecisionStatus.AccessDenied);
        }

        if (request.ResponsibleMembershipId == Guid.Empty)
        {
            return LegalDeadlineCreationPersistenceResult.Rejected(
                LegalDeadlineCreationDecisionStatus.RelatedResponsibleUnavailable);
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalProcess? legalProcess = await LockProcessAsync(
            dbContext,
            request.OrganizationId,
            request.ProcessId,
            cancellationToken);
        IEnumerable<Guid> membershipIds =
            request.ResponsibleMembershipId is Guid responsibleId
                ? [request.ActorMembershipId, responsibleId]
                : [request.ActorMembershipId];
        LegalTaskLockedIdentities identities = await LegalTaskIdentityLocking.LockAsync(
            dbContext,
            request.OrganizationId,
            membershipIds,
            cancellationToken);
        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        LegalDeadlineCreationDecision decision = decide(
            new LegalDeadlineCreationLockedState(
                organization?.IsActive == true,
                CreateMemberState(request.ActorMembershipId, identities),
                legalProcess is not null,
                request.ResponsibleMembershipId is Guid lockedResponsibleId
                    ? CreateMemberState(lockedResponsibleId, identities)
                    : null));

        if (decision.Status != LegalDeadlineCreationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineCreationPersistenceResult.Rejected(decision.Status);
        }

        if (decision.LegalDeadline is not { } legalDeadline ||
            legalDeadline.OrganizationId != request.OrganizationId ||
            legalDeadline.ProcessId != request.ProcessId ||
            legalDeadline.ResponsibleMembershipId != request.ResponsibleMembershipId ||
            !identities.MembershipsById.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership))
        {
            throw new InvalidOperationException(
                "A legal deadline persistence decision returned invalid state.");
        }

        await dbContext.LegalDeadlines.AddAsync(legalDeadline, cancellationToken);
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.LegalDeadlineCreated,
                legalDeadline.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDeadlineCreationPersistenceResult.Created(legalDeadline.Id);
    }

    private static LegalDeadlineLockedActorState? CreateMemberState(
        Guid membershipId,
        LegalTaskLockedIdentities identities)
    {
        if (!identities.MembershipsById.TryGetValue(
                membershipId,
                out OrganizationMembership? membership))
        {
            return null;
        }

        identities.UsersById.TryGetValue(membership.UserId, out User? user);
        return new LegalDeadlineLockedActorState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            user?.Id == membership.UserId && user.IsActive);
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
                WHERE id = {processId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<Organization?> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
