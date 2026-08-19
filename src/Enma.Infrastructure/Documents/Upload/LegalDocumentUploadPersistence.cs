using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Infrastructure.Persistence;

namespace Enma.Infrastructure.Documents.Upload;

public sealed class LegalDocumentUploadPersistence : ILegalDocumentUploadPersistence
{
    private static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(5);

    private readonly ILegalDocumentStorage storage;
    private readonly ILegalDocumentMetadataUploadTransaction metadataTransaction;

    public LegalDocumentUploadPersistence(
        ILegalDocumentStorage storage,
        ILegalDocumentMetadataUploadTransaction metadataTransaction)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(metadataTransaction);

        this.storage = storage;
        this.metadataTransaction = metadataTransaction;
    }

    public async Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
        LegalDocumentUploadPersistenceRequest request,
        Stream content,
        Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(decide);

        try
        {
            await storage.StoreAsync(
                request.ObjectKey,
                content,
                request.ContentLength,
                cancellationToken);
        }
        catch (LegalDocumentStorageObjectKeyConflictException)
        {
            // A conflict means the key already belongs to an existing object.
            // Never compensate by deleting an object that this attempt did not create.
            throw;
        }
        catch (OperationCanceledException)
        {
            await CompensatePreservingCancellationAsync(request.ObjectKey);
            throw;
        }
        catch (LegalDocumentStorageUnavailableException)
        {
            // The PUT outcome may be uncertain. Because the key is known before the
            // request, a bounded independent delete is safe for this upload attempt.
            await CompensateOrThrowAsync(request.ObjectKey);
            throw;
        }

        var attempt = new LegalDocumentMetadataUploadAttempt();
        LegalDocumentUploadPersistenceResult result;

        try
        {
            result = await metadataTransaction.ExecuteAsync(
                request,
                decide,
                attempt,
                cancellationToken);
        }
        catch (Exception) when (attempt.CommitStarted)
        {
            // Once COMMIT has started, PostgreSQL may have committed even when the
            // client observes a failure or cancellation. Deleting the object here
            // could create committed metadata pointing at a missing object.
            throw new LegalDocumentUploadOutcomeUnknownException();
        }
        catch (OperationCanceledException)
        {
            await CompensatePreservingCancellationAsync(request.ObjectKey);
            throw;
        }
        catch
        {
            // Before COMMIT starts, transaction disposal/rollback guarantees that no
            // committed metadata can reference the object, so compensation is safe.
            await CompensateOrThrowAsync(request.ObjectKey);
            throw;
        }

        // Keep compensation for a normal rejected result outside the transaction
        // exception-handling block. If cleanup itself fails, that failure must surface
        // exactly once rather than being caught as though the metadata transaction failed.
        if (result.Status != LegalDocumentUploadPersistenceResultStatus.Persisted)
        {
            await CompensateOrThrowAsync(request.ObjectKey);
        }

        return result;
    }

    private async Task CompensateOrThrowAsync(
        LegalDocumentStorageObjectKey objectKey)
    {
        using var compensationCancellation =
            new CancellationTokenSource(CompensationTimeout);

        try
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                compensationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new LegalDocumentUploadCompensationUnavailableException();
        }
        catch (LegalDocumentStorageUnavailableException)
        {
            throw new LegalDocumentUploadCompensationUnavailableException();
        }
    }

    private async Task CompensatePreservingCancellationAsync(
        LegalDocumentStorageObjectKey objectKey)
    {
        using var compensationCancellation =
            new CancellationTokenSource(CompensationTimeout);

        try
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                compensationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Preserve the caller-visible cancellation contract. A private orphan may
            // remain and is handled by the later reconciliation path.
        }
        catch (LegalDocumentStorageUnavailableException)
        {
            // Preserve cancellation for the same reason. The object remains private.
        }
    }
}
