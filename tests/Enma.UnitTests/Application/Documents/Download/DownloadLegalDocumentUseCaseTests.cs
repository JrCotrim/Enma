using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.Download;
using Enma.Application.Documents.Storage;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Documents.Download;

public sealed class DownloadLegalDocumentUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "6e38905a-e102-47fc-9579-f09f008dbd62");
    private static readonly Guid OrganizationId = Guid.Parse(
        "0d226bfa-3bc1-40b6-b011-7f3fba9f936b");
    private static readonly Guid MembershipId = Guid.Parse(
        "1f4f62d8-603c-4bca-a886-020f30979b94");
    private static readonly Guid DocumentId = Guid.Parse(
        "285d42cf-ec38-4107-a823-435c7b2af9ad");
    private const string PersistedObjectKey =
        "1234567890abcdef1234567890abcdef";

    [Fact]
    public async Task ExecuteAsync_WithSameTenantAccess_ReturnsTrustedStreamingContent()
    {
        byte[] payload = "private legal content"u8.ToArray();
        LegalDocumentContentReadModel document = CreateDocument(
            payload.LongLength);
        var queries = new FakeContentReadQueries(document);
        var handle = new TrackingStorageReadHandle(payload);
        var storage = new FakeStorage(handle);
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());

        Assert.Equal(
            DownloadLegalDocumentResultStatus.Succeeded,
            result.Status);
        LegalDocumentDownload download = Assert.IsType<LegalDocumentDownload>(
            result.Download);
        Assert.Equal(DocumentId, download.DocumentId);
        Assert.Equal("trusted-name.pdf", download.OriginalFileName);
        Assert.Equal("application/pdf", download.ContentType);
        Assert.Equal(payload.LongLength, download.SizeBytes);
        Assert.Same(handle.Content, download.Content);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(DocumentId, queries.DocumentId);
        Assert.Equal(
            LegalDocumentStorageObjectKey.Parse(PersistedObjectKey),
            storage.ObjectKey);

        await download.DisposeAsync();

        Assert.True(handle.IsDisposed);
        Assert.True(handle.TrackingContent.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = download.Content);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutLiveAccess_DeniesBeforeLookupAndStorage()
    {
        var queries = new FakeContentReadQueries(CreateDocument(1));
        var storage = new FakeStorage(new TrackingStorageReadHandle([1]));
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            null,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());

        Assert.Same(DownloadLegalDocumentResult.AccessDenied, result);
        Assert.Equal(0, queries.CallCount);
        Assert.Equal(0, storage.OpenReadCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MissingAndForeignIdentifiers_AreIndistinguishable()
    {
        var queries = new FakeContentReadQueries(null);
        var storage = new FakeStorage(new TrackingStorageReadHandle([1]));
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            storage);

        DownloadLegalDocumentResult missing = await useCase.ExecuteAsync(
            CreateQuery(Guid.Parse("f69920cd-9d19-4ede-b452-2a4316421c05")));
        DownloadLegalDocumentResult foreign = await useCase.ExecuteAsync(
            CreateQuery(Guid.Parse("69671be0-cd01-45bc-8332-0356d2c1eeef")));

        Assert.Same(DownloadLegalDocumentResult.NotFound, missing);
        Assert.Same(missing, foreign);
        Assert.Equal(2, queries.CallCount);
        Assert.Equal(0, storage.OpenReadCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyDocumentId_RejectsAfterAuthorizationWithoutLookup()
    {
        var queries = new FakeContentReadQueries(CreateDocument(1));
        var storage = new FakeStorage(new TrackingStorageReadHandle([1]));
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery(Guid.Empty));

        Assert.Same(DownloadLegalDocumentResult.InvalidInput, result);
        Assert.Equal(0, queries.CallCount);
        Assert.Equal(0, storage.OpenReadCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnavailableStorage_ReturnsSafeContentFailure()
    {
        var queries = new FakeContentReadQueries(CreateDocument(1));
        var storage = new FakeStorage(
            new LegalDocumentStorageObjectNotFoundException());
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());

        Assert.Same(DownloadLegalDocumentResult.ContentUnavailable, result);
        Assert.Null(result.Download);
        Assert.Equal(1, storage.OpenReadCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithLengthMismatch_DisposesStorageHandleAndFailsSafely()
    {
        var queries = new FakeContentReadQueries(CreateDocument(10));
        var handle = new TrackingStorageReadHandle([1, 2, 3]);
        var storage = new FakeStorage(handle);
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());

        Assert.Same(DownloadLegalDocumentResult.ContentUnavailable, result);
        Assert.True(handle.IsDisposed);
        Assert.True(handle.TrackingContent.IsDisposed);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidPersistedKey_FailsBeforeStorage()
    {
        LegalDocumentContentReadModel document = CreateDocument(
            1,
            storedObjectKey: "caller-controlled-path");
        var queries = new FakeContentReadQueries(document);
        var storage = new FakeStorage(new TrackingStorageReadHandle([1]));
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());

        Assert.Same(DownloadLegalDocumentResult.ContentUnavailable, result);
        Assert.Equal(0, storage.OpenReadCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOpenedHandleFails_DisposesPartialResource()
    {
        var handle = new FaultedStorageReadHandle();
        var queries = new FakeContentReadQueries(CreateDocument(1));
        var storage = new FakeStorage(handle);
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(CreateQuery()));

        Assert.True(handle.IsDisposed);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCancellationThroughAuthorizationQueryAndStorage()
    {
        var accessLookup = new StubAccessLookup(
            CreateAccess(OrganizationRole.Owner));
        var queries = new FakeContentReadQueries(CreateDocument(1));
        var storage = new FakeStorage(new TrackingStorageReadHandle([1]));
        var authorization = new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(accessLookup));
        var useCase = new DownloadLegalDocumentUseCase(
            authorization,
            queries,
            storage);
        using var cancellationTokenSource = new CancellationTokenSource();

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery(),
            cancellationTokenSource.Token);

        Assert.Equal(
            DownloadLegalDocumentResultStatus.Succeeded,
            result.Status);
        Assert.Equal(cancellationTokenSource.Token, accessLookup.Token);
        Assert.Equal(cancellationTokenSource.Token, queries.Token);
        Assert.Equal(cancellationTokenSource.Token, storage.Token);

        await result.Download!.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulDownload_DisposeIsIdempotent()
    {
        var handle = new TrackingStorageReadHandle([1]);
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeContentReadQueries(CreateDocument(1)),
            new FakeStorage(handle));
        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateQuery());
        LegalDocumentDownload download = Assert.IsType<LegalDocumentDownload>(
            result.Download);

        await download.DisposeAsync();
        await download.DisposeAsync();

        Assert.Equal(1, handle.DisposeCallCount);
    }

    [Fact]
    public void PublicDownloadContracts_DoNotAcceptOrExposeStorageLocator()
    {
        string[] queryProperties = typeof(DownloadLegalDocumentQuery)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["DocumentId", "OrganizationId", "UserId"],
            queryProperties);
        Assert.DoesNotContain(
            typeof(LegalDocumentDownload).GetProperties(),
            property => property.Name.Contains(
                "Key",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(LegalDocumentMetadataReadModel).GetProperties(),
            property => property.Name.Contains(
                "StoredObject",
                StringComparison.OrdinalIgnoreCase));
    }

    private static DownloadLegalDocumentUseCase CreateUseCase(
        OrganizationRole? role,
        FakeContentReadQueries queries,
        FakeStorage storage)
    {
        OrganizationAccessLookupResult? access = role.HasValue
            ? CreateAccess(role.Value)
            : null;
        var authorization = new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(
                new StubAccessLookup(access)));
        return new DownloadLegalDocumentUseCase(
            authorization,
            queries,
            storage);
    }

    private static OrganizationAccessLookupResult CreateAccess(
        OrganizationRole role)
    {
        return new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            MembershipId,
            role);
    }

    private static DownloadLegalDocumentQuery CreateQuery(
        Guid? documentId = null)
    {
        return new DownloadLegalDocumentQuery(
            UserId,
            OrganizationId,
            documentId ?? DocumentId);
    }

    private static LegalDocumentContentReadModel CreateDocument(
        long sizeBytes,
        string storedObjectKey = PersistedObjectKey)
    {
        return new LegalDocumentContentReadModel(
            DocumentId,
            "trusted-name.pdf",
            "application/pdf",
            sizeBytes,
            storedObjectKey);
    }

    private sealed class StubAccessLookup(
        OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
        public CancellationToken Token { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Task.FromResult(access);
        }
    }

    private sealed class FakeContentReadQueries(
        LegalDocumentContentReadModel? document)
        : ILegalDocumentContentReadQueries
    {
        public int CallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public Guid DocumentId { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<LegalDocumentContentReadModel?> FindAsync(
            Guid organizationId,
            Guid documentId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationId = organizationId;
            DocumentId = documentId;
            Token = cancellationToken;
            return Task.FromResult(document);
        }
    }

    private sealed class FakeStorage : ILegalDocumentStorage
    {
        private readonly ILegalDocumentStorageReadHandle? handle;
        private readonly LegalDocumentStorageException? exception;

        public FakeStorage(ILegalDocumentStorageReadHandle handle)
        {
            this.handle = handle;
        }

        public FakeStorage(LegalDocumentStorageException exception)
        {
            this.exception = exception;
        }

        public int OpenReadCallCount { get; private set; }

        public LegalDocumentStorageObjectKey? ObjectKey { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            OpenReadCallCount++;
            ObjectKey = objectKey;
            Token = cancellationToken;

            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(handle!);
        }

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }
    }

    private sealed class TrackingStorageReadHandle(byte[] content)
        : ILegalDocumentStorageReadHandle
    {
        public TrackingMemoryStream TrackingContent { get; } = new(content);

        public Stream Content => TrackingContent;

        public long ContentLength => content.LongLength;

        public int DisposeCallCount { get; private set; }

        public bool IsDisposed => DisposeCallCount > 0;

        public async ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            await TrackingContent.DisposeAsync();
        }
    }

    private sealed class TrackingMemoryStream(byte[] content)
        : MemoryStream(content, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FaultedStorageReadHandle
        : ILegalDocumentStorageReadHandle
    {
        public Stream Content => throw new InvalidOperationException();

        public long ContentLength => throw new InvalidOperationException();

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
