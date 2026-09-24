using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Documents.Delete;
using Enma.Application.Documents.Storage;
using Enma.Domain.Auditing;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDocumentDeletionPersistence
    : ILegalDocumentDeletionPersistence
{
    private readonly DbContextOptions<EnmaDbContext> dbContextOptions;
    private readonly TimeProvider timeProvider;

    public LegalDocumentDeletionPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.dbContextOptions = dbContextOptions;
        this.timeProvider = timeProvider;
    }

    public async Task<LegalDocumentDeletionPersistenceResult> RequestAsync(
        LegalDocumentDeletionPersistenceRequest request,
        Func<LegalDocumentDeletionLockedState, LegalDocumentDeletionDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        LegalDocument? document = await LockDocumentAsync(
            dbContext,
            request.OrganizationId,
            request.DocumentId,
            cancellationToken);
        (LegalDocumentDeletionActorState? State, OrganizationMembership? Membership)
            actor = await LockActorAsync(dbContext, request, cancellationToken);
        LegalDocumentDeletionDecision decision = decide(
            new LegalDocumentDeletionLockedState(
                actor.State,
                document is null
                    ? null
                    : new LegalDocumentDeletionDocumentState(
                        document.Id,
                        document.OrganizationId,
                        document.DeletionRequestedAt.HasValue)));

        if (decision != LegalDocumentDeletionDecision.Accept)
        {
            await transaction.RollbackAsync(cancellationToken);
            return decision switch
            {
                LegalDocumentDeletionDecision.AccessDenied =>
                    LegalDocumentDeletionPersistenceResult.AccessDenied,
                LegalDocumentDeletionDecision.NotFound =>
                    LegalDocumentDeletionPersistenceResult.NotFound,
                _ => throw new InvalidOperationException(
                    "Legal document deletion returned an invalid rejection decision.")
            };
        }

        if (document is null)
        {
            throw new InvalidOperationException(
                "An accepted legal document deletion must have a locked document.");
        }

        if (document.DeletionRequestedAt.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDocumentDeletionPersistenceResult.Accepted;
        }

        if (!document.RequestDeletion(request.RequestedAt))
        {
            throw new InvalidOperationException(
                "A new legal document deletion request was not applied.");
        }

        OrganizationMembership actorMembership = actor.Membership
            ?? throw new InvalidOperationException(
                "An accepted legal document deletion must have a validated actor.");
        AuditLogAppender.Append(
            dbContext,
            timeProvider,
            TransactionalAuditActorContext.FromValidatedMembership(
                actorMembership),
            new AuditIntent(
                AuditEventType.LegalDocumentDeleted,
                document.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDocumentDeletionPersistenceResult.Accepted;
    }

    public async Task<IReadOnlyList<PendingLegalDocumentDeletion>>
        ListPendingAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        var rows = await dbContext.LegalDocuments
            .AsNoTracking()
            .Where(document => document.DeletionRequestedAt != null)
            .OrderBy(document => document.DeletionRequestedAt)
            .ThenBy(document => document.Id)
            .Take(maximumCount)
            .Select(document => new
            {
                document.OrganizationId,
                DocumentId = document.Id,
                document.StoredObjectKey
            })
            .ToArrayAsync(cancellationToken);

        return rows.Select(row => new PendingLegalDocumentDeletion(
                row.OrganizationId,
                row.DocumentId,
                LegalDocumentStorageObjectKey.TryParse(
                    row.StoredObjectKey,
                    out LegalDocumentStorageObjectKey? objectKey) &&
                objectKey is not null
                    ? objectKey
                    : throw new InvalidOperationException(
                        "A pending legal document has an invalid storage key.")))
            .ToArray();
    }

    public async Task<bool> FinalizeAsync(
        PendingLegalDocumentDeletion deletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletion);

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        LegalDocument? document = await LockDocumentAsync(
            dbContext,
            deletion.OrganizationId,
            deletion.DocumentId,
            cancellationToken);

        if (document is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (!document.DeletionRequestedAt.HasValue ||
            !StringComparer.Ordinal.Equals(
                document.StoredObjectKey,
                deletion.ObjectKey.Value))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        dbContext.LegalDocuments.Remove(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<LegalDocument?> LockDocumentAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalDocuments
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_documents
                    WHERE organization_id = {organizationId}
                      AND id = {documentId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static async Task<(
        LegalDocumentDeletionActorState? State,
        OrganizationMembership? Membership)> LockActorAsync(
        EnmaDbContext dbContext,
        LegalDocumentDeletionPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        OrganizationMembership? membership =
            await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {request.OrganizationId}
                      AND id = {request.ActorMembershipId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);

        if (membership is null)
        {
            return (null, null);
        }

        User? user = await dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {membership.UserId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        Organization? organization = await dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {request.OrganizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        return (
            new LegalDocumentDeletionActorState(
                membership.UserId,
                membership.OrganizationId,
                membership.Id,
                membership.Role,
                membership.IsActive,
                user?.IsActive == true && user.Id == request.UserId,
                organization?.IsActive == true),
            membership);
    }
}
