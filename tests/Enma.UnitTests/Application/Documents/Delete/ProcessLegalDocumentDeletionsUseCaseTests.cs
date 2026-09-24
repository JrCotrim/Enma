using Enma.Application.Documents.Delete;
using Enma.Application.Documents.Storage;

namespace Enma.UnitTests.Application.Documents.Delete;

public sealed class ProcessLegalDocumentDeletionsUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WhenStorageDeletesObject_FinalizesMetadata()
    {
        PendingLegalDocumentDeletion deletion = CreateDeletion();
        var persistence = new StubDeletionPersistence([deletion]);
        var storage = new StubStorage();
        var useCase = new ProcessLegalDocumentDeletionsUseCase(
            persistence,
            storage);

        ProcessLegalDocumentDeletionsResult result =
            await useCase.ExecuteAsync();

        Assert.Equal(new(1, 1, 0), result);
        Assert.Equal([deletion], persistence.Finalized);
        Assert.Equal([deletion.ObjectKey], storage.DeletedKeys);
        Assert.Equal(
            ProcessLegalDocumentDeletionsUseCase.BatchSize,
            persistence.MaximumCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageIsUnavailable_LeavesMetadataPending()
    {
        PendingLegalDocumentDeletion deletion = CreateDeletion();
        var persistence = new StubDeletionPersistence([deletion]);
        var storage = new StubStorage { IsUnavailable = true };
        var useCase = new ProcessLegalDocumentDeletionsUseCase(
            persistence,
            storage);

        ProcessLegalDocumentDeletionsResult result =
            await useCase.ExecuteAsync();

        Assert.Equal(new(1, 0, 1), result);
        Assert.Empty(persistence.Finalized);
        Assert.Equal([deletion.ObjectKey], storage.DeletedKeys);
    }

    private static PendingLegalDocumentDeletion CreateDeletion() => new(
        Guid.Parse("25611b18-727f-4bca-af83-1c169adf6143"),
        Guid.Parse("7500d38b-91dd-42d9-b05e-d2ead47ba63c"),
        LegalDocumentStorageObjectKey.Parse(
            "0123456789abcdef0123456789abcdef"));

    private sealed class StubDeletionPersistence(
        IReadOnlyList<PendingLegalDocumentDeletion> pending)
        : ILegalDocumentDeletionPersistence
    {
        public int MaximumCount { get; private set; }

        public List<PendingLegalDocumentDeletion> Finalized { get; } = [];

        public Task<LegalDocumentDeletionPersistenceResult> RequestAsync(
            LegalDocumentDeletionPersistenceRequest request,
            Func<LegalDocumentDeletionLockedState, LegalDocumentDeletionDecision> decide,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PendingLegalDocumentDeletion>> ListPendingAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            MaximumCount = maximumCount;
            return Task.FromResult(pending);
        }

        public Task<bool> FinalizeAsync(
            PendingLegalDocumentDeletion deletion,
            CancellationToken cancellationToken = default)
        {
            Finalized.Add(deletion);
            return Task.FromResult(true);
        }
    }

    private sealed class StubStorage : ILegalDocumentStorage
    {
        public bool IsUnavailable { get; init; }

        public List<LegalDocumentStorageObjectKey> DeletedKeys { get; } = [];

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            return IsUnavailable
                ? Task.FromException(
                    new LegalDocumentStorageUnavailableException())
                : Task.CompletedTask;
        }
    }
}
