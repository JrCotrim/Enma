using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Domain.Documents;
using Enma.Infrastructure.Documents.Upload;
using Enma.Infrastructure.Persistence;

namespace Enma.IntegrationTests.Infrastructure.Documents;

public sealed class LegalDocumentUploadPersistenceTests
{
    [Fact]
    public async Task ExecuteAsync_PersistedMetadata_KeepsObjectAndReturnsDocumentId()
    {
        var storage = new FakeStorage();
        Guid documentId = Guid.NewGuid();
        var metadata = new FakeMetadataTransaction
        {
            Result = LegalDocumentUploadPersistenceResult.Persisted(documentId)
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        LegalDocumentUploadPersistenceResult result = await persistence.ExecuteAsync(
            request,
            content,
            _ => LegalDocumentUploadDecision.AccessDenied);

        Assert.Equal(LegalDocumentUploadPersistenceResultStatus.Persisted, result.Status);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(0, storage.DeleteCallCount);
        Assert.Equal(1, metadata.ExecuteCallCount);
        Assert.Same(request.ObjectKey, storage.StoredObjectKey);
        Assert.Same(content, storage.StoredContent);
        Assert.Equal(request.ContentLength, storage.StoredContentLength);
        Assert.False(content.IsDisposedForTest());
    }

    [Theory]
    [InlineData(LegalDocumentUploadPersistenceResultStatus.AccessDenied)]
    [InlineData(LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable)]
    [InlineData(LegalDocumentUploadPersistenceResultStatus.RelatedProcessUnavailable)]
    public async Task ExecuteAsync_RejectedMetadata_CompensatesAndReturnsRejection(
        LegalDocumentUploadPersistenceResultStatus status)
    {
        var storage = new FakeStorage();
        var metadata = new FakeMetadataTransaction
        {
            Result = CreateRejectedResult(status)
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        LegalDocumentUploadPersistenceResult result = await persistence.ExecuteAsync(
            request,
            content,
            _ => LegalDocumentUploadDecision.AccessDenied);

        Assert.Equal(status, result.Status);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.DeleteCallCount);
        Assert.Same(request.ObjectKey, storage.DeletedObjectKey);
        Assert.True(storage.DeleteCancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task ExecuteAsync_StorageUnavailable_CompensatesBeforeRethrowing()
    {
        var storage = new FakeStorage
        {
            StoreException = new LegalDocumentStorageUnavailableException()
        };
        var metadata = new FakeMetadataTransaction();
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<LegalDocumentStorageUnavailableException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied));

        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.DeleteCallCount);
        Assert.Equal(0, metadata.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ObjectKeyConflict_DoesNotDeleteExistingObject()
    {
        var storage = new FakeStorage
        {
            StoreException = new LegalDocumentStorageObjectKeyConflictException()
        };
        var metadata = new FakeMetadataTransaction();
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<LegalDocumentStorageObjectKeyConflictException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied));

        Assert.Equal(0, storage.DeleteCallCount);
        Assert.Equal(0, metadata.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_StorageCancellation_UsesIndependentCleanupTokenAndPreservesCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        var storage = new FakeStorage
        {
            StoreException = new OperationCanceledException(callerCancellation.Token)
        };
        var metadata = new FakeMetadataTransaction();
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied,
                callerCancellation.Token));

        Assert.Equal(1, storage.DeleteCallCount);
        Assert.NotEqual(callerCancellation.Token, storage.DeleteCancellationToken);
        Assert.True(storage.DeleteCancellationToken.CanBeCanceled);
        Assert.Equal(0, metadata.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataFailureBeforeCommit_CompensatesAndRethrows()
    {
        var storage = new FakeStorage();
        var expected = new InvalidOperationException("synthetic metadata failure");
        var metadata = new FakeMetadataTransaction
        {
            Exception = expected
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied));

        Assert.Same(expected, actual);
        Assert.Equal(1, storage.DeleteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataCancellationBeforeCommit_CompensatesAndPreservesCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        var storage = new FakeStorage();
        var metadata = new FakeMetadataTransaction
        {
            Exception = new OperationCanceledException(callerCancellation.Token)
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied,
                callerCancellation.Token));

        Assert.Equal(1, storage.DeleteCallCount);
        Assert.NotEqual(callerCancellation.Token, storage.DeleteCancellationToken);
        Assert.True(storage.DeleteCancellationToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailureAfterCommitStarted_PreservesObjectAndReturnsUnknownOutcome(
        bool cancellation)
    {
        using var callerCancellation = new CancellationTokenSource();
        if (cancellation)
        {
            callerCancellation.Cancel();
        }

        var storage = new FakeStorage();
        var metadata = new FakeMetadataTransaction
        {
            MarkCommitStartedBeforeFailure = true,
            Exception = cancellation
                ? new OperationCanceledException(callerCancellation.Token)
                : new InvalidOperationException("synthetic ambiguous commit")
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        LegalDocumentUploadOutcomeUnknownException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadOutcomeUnknownException>(
                () => persistence.ExecuteAsync(
                    request,
                    content,
                    _ => LegalDocumentUploadDecision.AccessDenied,
                    callerCancellation.Token));

        Assert.Contains("could not be confirmed", exception.Message);
        Assert.Contains("Do not automatically retry", exception.Message);
        Assert.DoesNotContain("synthetic ambiguous commit", exception.Message);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, metadata.ExecuteCallCount);
        Assert.Equal(0, storage.DeleteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_CompensationStorageUnavailable_ReturnsSafeCleanupFailure()
    {
        var storage = new FakeStorage
        {
            DeleteException = new LegalDocumentStorageUnavailableException()
        };
        var metadata = new FakeMetadataTransaction
        {
            Result = LegalDocumentUploadPersistenceResult.AccessDenied
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        LegalDocumentUploadCompensationUnavailableException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadCompensationUnavailableException>(
                () => persistence.ExecuteAsync(
                    request,
                    content,
                    _ => LegalDocumentUploadDecision.AccessDenied));

        Assert.Contains("cleanup is temporarily unavailable", exception.Message);
        Assert.Equal(1, storage.DeleteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_CompensationTimeout_ReturnsSafeCleanupFailure()
    {
        var storage = new FakeStorage
        {
            CancelDeleteUsingProvidedToken = true
        };
        var metadata = new FakeMetadataTransaction
        {
            Result = LegalDocumentUploadPersistenceResult.AccessDenied
        };
        var persistence = new LegalDocumentUploadPersistence(storage, metadata);
        LegalDocumentUploadPersistenceRequest request = CreateRequest();
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<LegalDocumentUploadCompensationUnavailableException>(
            () => persistence.ExecuteAsync(
                request,
                content,
                _ => LegalDocumentUploadDecision.AccessDenied));

        Assert.Equal(1, storage.DeleteCallCount);
        Assert.True(storage.DeleteCancellationToken.CanBeCanceled);
    }

    private static LegalDocumentUploadPersistenceRequest CreateRequest()
    {
        return new LegalDocumentUploadPersistenceRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "contract.pdf",
            LegalDocumentStorageObjectKey.CreateNew(),
            "application/pdf",
            3,
            new LegalDocumentContentHash(new byte[32]));
    }

    private static LegalDocumentUploadPersistenceResult CreateRejectedResult(
        LegalDocumentUploadPersistenceResultStatus status)
    {
        return status switch
        {
            LegalDocumentUploadPersistenceResultStatus.AccessDenied =>
                LegalDocumentUploadPersistenceResult.AccessDenied,
            LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable =>
                LegalDocumentUploadPersistenceResult.RelatedClientUnavailable,
            LegalDocumentUploadPersistenceResultStatus.RelatedProcessUnavailable =>
                LegalDocumentUploadPersistenceResult.RelatedProcessUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }

    private sealed class FakeStorage : ILegalDocumentStorage
    {
        public Exception? StoreException { get; init; }

        public Exception? DeleteException { get; init; }

        public bool CancelDeleteUsingProvidedToken { get; init; }

        public int StoreCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public LegalDocumentStorageObjectKey? StoredObjectKey { get; private set; }

        public Stream? StoredContent { get; private set; }

        public long StoredContentLength { get; private set; }

        public LegalDocumentStorageObjectKey? DeletedObjectKey { get; private set; }

        public CancellationToken DeleteCancellationToken { get; private set; }

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default)
        {
            StoreCallCount++;
            StoredObjectKey = objectKey;
            StoredContent = content;
            StoredContentLength = contentLength;

            return StoreException is null
                ? Task.CompletedTask
                : Task.FromException(StoreException);
        }

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            DeletedObjectKey = objectKey;
            DeleteCancellationToken = cancellationToken;

            if (CancelDeleteUsingProvidedToken)
            {
                return Task.FromException(
                    new OperationCanceledException(cancellationToken));
            }

            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }
    }

    private sealed class FakeMetadataTransaction : ILegalDocumentMetadataUploadTransaction
    {
        public LegalDocumentUploadPersistenceResult Result { get; init; } =
            LegalDocumentUploadPersistenceResult.Persisted(Guid.NewGuid());

        public Exception? Exception { get; init; }

        public bool MarkCommitStartedBeforeFailure { get; init; }

        public int ExecuteCallCount { get; private set; }

        public Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
            LegalDocumentUploadPersistenceRequest request,
            Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
            LegalDocumentMetadataUploadAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            ExecuteCallCount++;

            if (MarkCommitStartedBeforeFailure)
            {
                attempt.MarkCommitStarted();
            }

            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<LegalDocumentUploadPersistenceResult>(Exception);
        }
    }
}

internal static class MemoryStreamTestExtensions
{
    public static bool IsDisposedForTest(this MemoryStream stream)
    {
        try
        {
            _ = stream.Position;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
