using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Processes;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalProcessMutationPersistence
    : ILegalProcessMutationPersistence,
        ILegalProcessOperationalMutationPersistence
{
    private const string NormalizedProcessNumberConstraint =
        "ux_legal_processes_organization_id_normalized_process_number";

    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalProcessMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        LegalProcessMutationPersistenceRequest request,
        Func<LegalProcessMutationLockedState, LegalProcessMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.ProcessId == Guid.Empty)
        {
            return LegalProcessMutationPersistenceResult.AccessDenied;
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalProcess? legalProcess = (await dbContext.LegalProcesses
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_processes
                    WHERE id = {request.ProcessId}
                      AND organization_id = {request.OrganizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (legalProcess is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessMutationPersistenceResult.NotFound;
        }

        string oldTitle = legalProcess.Title;
        OrganizationMembership? actorMembership = await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            cancellationToken);
        User? actorUser = actorMembership is null
            ? null
            : await LockActorUserAsync(
                dbContext,
                actorMembership.UserId,
                cancellationToken);
        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        LegalProcessMutationDecision decision = decide(
            new LegalProcessMutationLockedState(
                legalProcess,
                organization?.IsActive == true,
                CreateActorState(actorMembership, actorUser)));

        if (decision.Status != LegalProcessMutationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessMutationPersistenceResult.AccessDenied;
        }

        if (StringComparer.Ordinal.Equals(oldTitle, legalProcess.Title))
        {
            await transaction.CommitAsync(cancellationToken);
            return LegalProcessMutationPersistenceResult.Updated;
        }

        if (actorMembership is null)
        {
            throw new InvalidOperationException(
                "A legal process mutation accepted a missing actor.");
        }

        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.LegalProcessTitleChanged,
                legalProcess.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalProcessMutationPersistenceResult.Updated;
    }

    public async Task<LegalProcessOperationalMutationPersistenceResult>
        ChangeOperationalAsync(
            LegalProcessOperationalMutationPersistenceRequest request,
            Func<LegalProcess, Guid?> selectRelatedMembershipToLock,
            Func<LegalProcessOperationalMutationLockedState,
                LegalProcessOperationalMutationDecision> decide,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectRelatedMembershipToLock);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.ProcessId == Guid.Empty ||
            !Enum.IsDefined(request.Mutation))
        {
            return LegalProcessOperationalMutationPersistenceResult.AccessDenied;
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalProcess? legalProcess = await LockLegalProcessAsync(
            dbContext,
            request.OrganizationId,
            request.ProcessId,
            cancellationToken);

        if (legalProcess is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessOperationalMutationPersistenceResult.NotFound;
        }

        LegalProcessSnapshot before = LegalProcessSnapshot.From(legalProcess);
        Guid? relatedMembershipId = selectRelatedMembershipToLock(legalProcess);

        if (relatedMembershipId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A related membership lock selection cannot contain an empty id.");
        }

        IEnumerable<Guid> membershipIds = relatedMembershipId is Guid relatedId
            ? [request.ActorMembershipId, relatedId]
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
        LegalProcessOperationalMutationDecision decision = decide(
            new LegalProcessOperationalMutationLockedState(
                legalProcess,
                organization?.IsActive == true,
                CreateMemberState(request.ActorMembershipId, identities),
                relatedMembershipId is Guid lockedRelatedId
                    ? CreateMemberState(lockedRelatedId, identities)
                    : null));

        if (decision != LegalProcessOperationalMutationDecision.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MapRejectedDecision(decision);
        }

        AuditIntent? auditIntent = CreateAuditIntent(
            legalProcess,
            before,
            request.Mutation);

        if (auditIntent is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return LegalProcessOperationalMutationPersistenceResult.Succeeded;
        }

        if (!identities.MembershipsById.TryGetValue(
                request.ActorMembershipId,
                out OrganizationMembership? actorMembership))
        {
            throw new InvalidOperationException(
                "A legal process mutation accepted a missing actor.");
        }

        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership),
            auditIntent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            request.Mutation == LegalProcessOperationalMutation.Details &&
            IsUniqueViolation(exception, NormalizedProcessNumberConstraint))
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessOperationalMutationPersistenceResult
                .DuplicateProcessNumber;
        }

        await transaction.CommitAsync(cancellationToken);
        return LegalProcessOperationalMutationPersistenceResult.Succeeded;
    }

    private static AuditIntent? CreateAuditIntent(
        LegalProcess legalProcess,
        LegalProcessSnapshot before,
        LegalProcessOperationalMutation mutation)
    {
        LegalProcessSnapshot after = LegalProcessSnapshot.From(legalProcess);

        if (after.Id != before.Id ||
            after.OrganizationId != before.OrganizationId ||
            after.ClientId != before.ClientId ||
            after.CreatedAt != before.CreatedAt ||
            !StringComparer.Ordinal.Equals(after.Title, before.Title))
        {
            throw new InvalidOperationException(
                "A legal process operational decision changed immutable fields.");
        }

        if (StringComparer.Ordinal.Equals(after.ProcessNumber, before.ProcessNumber) &&
            !StringComparer.Ordinal.Equals(
                after.NormalizedProcessNumber,
                before.NormalizedProcessNumber))
        {
            throw new InvalidOperationException(
                "A legal process normalized number changed without its display value.");
        }

        bool detailsChanged =
            !StringComparer.Ordinal.Equals(after.ProcessNumber, before.ProcessNumber) ||
            !StringComparer.Ordinal.Equals(
                after.CourtOrAuthority,
                before.CourtOrAuthority);
        bool statusChanged = after.Status != before.Status;
        bool responsibleChanged =
            after.ResponsibleMembershipId != before.ResponsibleMembershipId;

        if (detailsChanged && mutation != LegalProcessOperationalMutation.Details ||
            statusChanged && mutation != LegalProcessOperationalMutation.Status ||
            responsibleChanged && mutation != LegalProcessOperationalMutation.Responsible)
        {
            throw new InvalidOperationException(
                "A legal process operational decision changed another field group.");
        }

        return mutation switch
        {
            LegalProcessOperationalMutation.Details when detailsChanged =>
                CreateDetailsChangedIntent(legalProcess.Id, before, after),
            LegalProcessOperationalMutation.Status when statusChanged =>
                new AuditIntent(
                    AuditEventType.LegalProcessStatusChanged,
                    legalProcess.Id,
                    new LegalProcessStatusChangedAuditDetails(
                        before.Status,
                        after.Status)),
            LegalProcessOperationalMutation.Responsible when responsibleChanged =>
                new AuditIntent(
                    AuditEventType.LegalProcessResponsibleChanged,
                    legalProcess.Id,
                    new LegalProcessResponsibleChangedAuditDetails(
                        before.ResponsibleMembershipId,
                        after.ResponsibleMembershipId)),
            _ => null
        };
    }

    private static AuditIntent CreateDetailsChangedIntent(
        Guid legalProcessId,
        LegalProcessSnapshot before,
        LegalProcessSnapshot after)
    {
        var changedFields = new List<LegalProcessChangedField>(2);

        if (!StringComparer.Ordinal.Equals(after.ProcessNumber, before.ProcessNumber))
        {
            changedFields.Add(LegalProcessChangedField.ProcessNumber);
        }

        if (!StringComparer.Ordinal.Equals(
                after.CourtOrAuthority,
                before.CourtOrAuthority))
        {
            changedFields.Add(LegalProcessChangedField.CourtOrAuthority);
        }

        return new AuditIntent(
            AuditEventType.LegalProcessDetailsChanged,
            legalProcessId,
            new LegalProcessDetailsChangedAuditDetails(changedFields));
    }

    private static LegalProcessOperationalMutationPersistenceResult MapRejectedDecision(
        LegalProcessOperationalMutationDecision decision)
    {
        return decision switch
        {
            LegalProcessOperationalMutationDecision.AccessDenied =>
                LegalProcessOperationalMutationPersistenceResult.AccessDenied,
            LegalProcessOperationalMutationDecision.RelatedResponsibleUnavailable =>
                LegalProcessOperationalMutationPersistenceResult
                    .RelatedResponsibleUnavailable,
            LegalProcessOperationalMutationDecision.StatusTransitionNotAllowed =>
                LegalProcessOperationalMutationPersistenceResult
                    .StatusTransitionNotAllowed,
            LegalProcessOperationalMutationDecision.CurrentResponsibleUnavailable =>
                LegalProcessOperationalMutationPersistenceResult
                    .CurrentResponsibleUnavailable,
            _ => throw new InvalidOperationException(
                "Legal process mutation returned an invalid decision.")
        };
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
        return CreateActorState(membership, user);
    }

    private static async Task<LegalProcess?> LockLegalProcessAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalProcesses
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_processes
                    WHERE id = {processId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static bool IsUniqueViolation(
        Exception exception,
        string constraintName)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                } postgresException &&
                string.Equals(
                    postgresException.ConstraintName,
                    constraintName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record LegalProcessSnapshot(
        Guid Id,
        Guid OrganizationId,
        Guid ClientId,
        string Title,
        string? ProcessNumber,
        string? NormalizedProcessNumber,
        LegalProcessStatus Status,
        string? CourtOrAuthority,
        Guid? ResponsibleMembershipId,
        DateTimeOffset CreatedAt)
    {
        public static LegalProcessSnapshot From(LegalProcess legalProcess)
        {
            return new LegalProcessSnapshot(
                legalProcess.Id,
                legalProcess.OrganizationId,
                legalProcess.ClientId,
                legalProcess.Title,
                legalProcess.ProcessNumber,
                legalProcess.NormalizedProcessNumber,
                legalProcess.Status,
                legalProcess.CourtOrAuthority,
                legalProcess.ResponsibleMembershipId,
                legalProcess.CreatedAt);
        }
    }

    private static LegalProcessLockedActorState? CreateActorState(
        OrganizationMembership? membership,
        User? user)
    {
        return membership is null
            ? null
            : new LegalProcessLockedActorState(
                membership.Id,
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                user?.Id == membership.UserId && user.IsActive);
    }

    private static Task<OrganizationMembership?> LockActorMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {organizationId}
                  AND id = {actorMembershipId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<User?> LockActorUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {userId}
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
