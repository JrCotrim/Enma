using Enma.Application.Documents.Storage;

namespace Enma.Application.Documents.Delete;

public sealed class ProcessLegalDocumentDeletionsUseCase
{
    public const int BatchSize = 25;

    private readonly ILegalDocumentDeletionPersistence deletionPersistence;
    private readonly ILegalDocumentStorage storage;

    public ProcessLegalDocumentDeletionsUseCase(
        ILegalDocumentDeletionPersistence deletionPersistence,
        ILegalDocumentStorage storage)
    {
        ArgumentNullException.ThrowIfNull(deletionPersistence);
        ArgumentNullException.ThrowIfNull(storage);

        this.deletionPersistence = deletionPersistence;
        this.storage = storage;
    }

    public async Task<ProcessLegalDocumentDeletionsResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PendingLegalDocumentDeletion> pending =
            await deletionPersistence.ListPendingAsync(
                BatchSize,
                cancellationToken);
        int completed = 0;
        int deferred = 0;

        foreach (PendingLegalDocumentDeletion deletion in pending)
        {
            try
            {
                await storage.DeleteIfExistsAsync(
                    deletion.ObjectKey,
                    cancellationToken);
                if (await deletionPersistence.FinalizeAsync(
                        deletion,
                        cancellationToken))
                {
                    completed++;
                }
            }
            catch (LegalDocumentStorageUnavailableException)
            {
                deferred++;
            }
        }

        return new ProcessLegalDocumentDeletionsResult(
            pending.Count,
            completed,
            deferred);
    }
}
