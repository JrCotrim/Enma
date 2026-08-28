using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Documents.Upload;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDocumentMetadataUploadTransaction
    : ILegalDocumentMetadataUploadTransaction
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public LegalDocumentMetadataUploadTransaction(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
        LegalDocumentUploadPersistenceRequest request,
        Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
        LegalDocumentMetadataUploadAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateRequestShape(request);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Client? client = null;
        LegalProcess? process = null;

        if (request.ClientId is Guid clientId)
        {
            client = await LockClientAsync(
                dbContext,
                request.OrganizationId,
                clientId,
                cancellationToken);
        }
        else if (request.ProcessId is Guid processId)
        {
            process = await LockProcessAsync(
                dbContext,
                request.OrganizationId,
                processId,
                cancellationToken);
        }

        (LegalDocumentUploadActorState? State, OrganizationMembership? Membership)
            actor = await LockActorAsync(dbContext, request, cancellationToken);

        LegalDocumentUploadDecision decision = decide(
            new LegalDocumentUploadLockedState(
                actor.State,
                CreateClientState(client),
                CreateProcessState(process)));

        if (decision.Status != LegalDocumentUploadDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MapRejectedDecision(decision.Status);
        }

        LegalDocument legalDocument = decision.LegalDocument
            ?? throw new InvalidOperationException(
                "A persistence decision must include a legal document.");

        EnsureDocumentMatchesRequest(legalDocument, request);

        await dbContext.LegalDocuments.AddAsync(
            legalDocument,
            cancellationToken);
        OrganizationMembership actorMembership = actor.Membership
            ?? throw new InvalidOperationException(
                "A persisted legal document must have a validated actor membership.");
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.LegalDocumentUploaded,
                legalDocument.Id));
        await dbContext.SaveChangesAsync(cancellationToken);

        attempt.MarkCommitStarted();
        await transaction.CommitAsync(cancellationToken);

        return LegalDocumentUploadPersistenceResult.Persisted(
            legalDocument.Id);
    }

    private static async Task<Client?> LockClientAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Clients
            .FromSqlInterpolated(
                $"""
                SELECT * FROM clients
                WHERE organization_id = {organizationId}
                  AND id = {clientId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<LegalProcess?> LockProcessAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        return await dbContext.LegalProcesses
            .FromSqlInterpolated(
                $"""
                SELECT * FROM legal_processes
                WHERE organization_id = {organizationId}
                  AND id = {processId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<(
        LegalDocumentUploadActorState? State,
        OrganizationMembership? Membership)> LockActorAsync(
        EnmaDbContext dbContext,
        LegalDocumentUploadPersistenceRequest request,
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

        Organization? organization =
            await dbContext.Organizations
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organizations
                    WHERE id = {request.OrganizationId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);

        return (
            new LegalDocumentUploadActorState(
                membership.UserId,
                membership.OrganizationId,
                membership.Id,
                membership.Role,
                membership.IsActive,
                user?.IsActive == true &&
                    user.Id == request.UserId &&
                    membership.UserId == request.UserId,
                organization?.IsActive == true &&
                    organization.Id == request.OrganizationId),
            membership);
    }

    private static LegalDocumentUploadClientState? CreateClientState(
        Client? client)
    {
        return client is null
            ? null
            : new LegalDocumentUploadClientState(
                client.Id,
                client.OrganizationId,
                client.IsActive);
    }

    private static LegalDocumentUploadProcessState? CreateProcessState(
        LegalProcess? process)
    {
        return process is null
            ? null
            : new LegalDocumentUploadProcessState(
                process.Id,
                process.OrganizationId);
    }

    private static LegalDocumentUploadPersistenceResult MapRejectedDecision(
        LegalDocumentUploadDecisionStatus status)
    {
        return status switch
        {
            LegalDocumentUploadDecisionStatus.AccessDenied =>
                LegalDocumentUploadPersistenceResult.AccessDenied,
            LegalDocumentUploadDecisionStatus.RelatedClientUnavailable =>
                LegalDocumentUploadPersistenceResult.RelatedClientUnavailable,
            LegalDocumentUploadDecisionStatus.RelatedProcessUnavailable =>
                LegalDocumentUploadPersistenceResult.RelatedProcessUnavailable,
            _ => throw new InvalidOperationException(
                "Legal document metadata transaction received an invalid rejection decision.")
        };
    }

    private static void ValidateRequestShape(
        LegalDocumentUploadPersistenceRequest request)
    {
        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException(
                "User id cannot be empty.",
                nameof(request));
        }

        if (request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id cannot be empty.",
                nameof(request));
        }

        if (request.ActorMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                "Actor membership id cannot be empty.",
                nameof(request));
        }

        if (request.ClientId == Guid.Empty ||
            request.ProcessId == Guid.Empty ||
            request.ClientId.HasValue && request.ProcessId.HasValue)
        {
            throw new ArgumentException(
                "Legal document classification is invalid.",
                nameof(request));
        }
    }

    private static void EnsureDocumentMatchesRequest(
        LegalDocument legalDocument,
        LegalDocumentUploadPersistenceRequest request)
    {
        bool matches =
            legalDocument.OrganizationId == request.OrganizationId &&
            legalDocument.ClientId == request.ClientId &&
            legalDocument.ProcessId == request.ProcessId &&
            string.Equals(
                legalDocument.OriginalFileName,
                request.OriginalFileName,
                StringComparison.Ordinal) &&
            string.Equals(
                legalDocument.StoredObjectKey,
                request.ObjectKey.Value,
                StringComparison.Ordinal) &&
            string.Equals(
                legalDocument.ContentType,
                request.CanonicalContentType,
                StringComparison.Ordinal) &&
            legalDocument.SizeBytes == request.ContentLength &&
            legalDocument.ContentHashSha256.Equals(
                request.ContentHashSha256) &&
            legalDocument.UploadedByMembershipId ==
                request.ActorMembershipId;

        if (!matches)
        {
            throw new InvalidOperationException(
                "The legal document persistence decision does not match the validated upload request.");
        }
    }
}
