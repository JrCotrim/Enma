using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Processes;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalProcessCreationPersistence
    : ILegalProcessCreationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalProcessCreationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<LegalProcessCreationPersistenceResult> ExecuteAsync(
        LegalProcessCreationPersistenceRequest request,
        Func<LegalProcessCreationLockedState, LegalProcessCreationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.ClientId == Guid.Empty)
        {
            return LegalProcessCreationPersistenceResult.Rejected(
                LegalProcessCreationDecisionStatus.AccessDenied);
        }

        if (request.ResponsibleMembershipId == Guid.Empty)
        {
            return LegalProcessCreationPersistenceResult.Rejected(
                LegalProcessCreationDecisionStatus.RelatedResponsibleUnavailable);
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Client? client = await LockClientAsync(
            dbContext,
            request.OrganizationId,
            request.ClientId,
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
        LegalProcessCreationDecision decision = decide(
            new LegalProcessCreationLockedState(
                organization?.IsActive == true,
                CreateMemberState(request.ActorMembershipId, identities),
                client?.IsActive == true,
                request.ResponsibleMembershipId is Guid lockedResponsibleId
                    ? CreateMemberState(lockedResponsibleId, identities)
                    : null));

        if (decision.Status != LegalProcessCreationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessCreationPersistenceResult.Rejected(decision.Status);
        }

        if (decision.LegalProcess is not { } legalProcess ||
            legalProcess.OrganizationId != request.OrganizationId ||
            legalProcess.ClientId != request.ClientId ||
            legalProcess.ResponsibleMembershipId != request.ResponsibleMembershipId ||
            !identities.MembershipsById.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership))
        {
            throw new InvalidOperationException(
                "A legal process persistence decision returned invalid state.");
        }

        await dbContext.LegalProcesses.AddAsync(legalProcess, cancellationToken);
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.LegalProcessCreated,
                legalProcess.Id));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            LegalProcessPersistenceConstraints.IsUniqueViolation(
                exception,
                LegalProcessPersistenceConstraints.NormalizedProcessNumber))
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessCreationPersistenceResult.Rejected(
                LegalProcessCreationDecisionStatus.DuplicateProcessNumber);
        }

        await transaction.CommitAsync(cancellationToken);

        return LegalProcessCreationPersistenceResult.Created(legalProcess.Id);
    }

    private static LegalProcessLockedActorState? CreateMemberState(
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
        return new LegalProcessLockedActorState(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Role,
            membership.IsActive,
            user?.Id == membership.UserId && user.IsActive);
    }

    private static Task<Client?> LockClientAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        return dbContext.Clients
            .FromSqlInterpolated(
                $"""
                SELECT * FROM clients
                WHERE id = {clientId}
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
