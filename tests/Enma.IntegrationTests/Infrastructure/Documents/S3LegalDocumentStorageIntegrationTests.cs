using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Enma.Application.Documents.Storage;
using Enma.Infrastructure.Documents.Storage;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Documents;

[Collection(MinioStorageCollection.Name)]
public sealed class S3LegalDocumentStorageIntegrationTests : IAsyncLifetime
{
    private readonly DocumentStorageIntegrationEnvironment environment;
    private readonly AmazonS3Client applicationClient;
    private readonly S3LegalDocumentStorage storage;

    public S3LegalDocumentStorageIntegrationTests()
    {
        environment = DocumentStorageIntegrationEnvironment.Load();
        applicationClient = new AmazonS3Client(
            new BasicAWSCredentials(
                environment.AppAccessKey,
                environment.AppSecretKey),
            new AmazonS3Config
            {
                ServiceURL = environment.ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion =
                    DocumentStorageIntegrationEnvironment.Region
            });

        storage = new S3LegalDocumentStorage(
            applicationClient,
            Options.Create(
                new DocumentStorageOptions
                {
                    ServiceUrl = environment.ServiceUrl,
                    BucketName =
                        DocumentStorageIntegrationEnvironment.BucketName,
                    Region =
                        DocumentStorageIntegrationEnvironment.Region,
                    ForcePathStyle = true,
                    AccessKey = environment.AppAccessKey,
                    SecretKey = environment.AppSecretKey,
                    RequireTls = false
                }));
    }

    [Fact]
    public async Task StoreAndOpen_RoundTripsExactBytesAndLength_WithoutClosingInputStream()
    {
        byte[] payload = "ENMA private legal document storage test"u8.ToArray();
        using var input = new MemoryStream(payload, writable: false);

        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();

        await storage.StoreAsync(
            objectKey,
            input,
            payload.LongLength,
            CancellationToken.None);

        try
        {
            Assert.True(input.CanRead);
            AssertOpaqueObjectKey(objectKey);

            ILegalDocumentStorageReadHandle handle = await storage.OpenReadAsync(
                objectKey,
                CancellationToken.None);

            Assert.Equal(payload.LongLength, handle.ContentLength);

            using var copy = new MemoryStream();
            await handle.Content.CopyToAsync(
                copy,
                CancellationToken.None);

            Assert.Equal(payload, copy.ToArray());

            await handle.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(
                () => _ = handle.Content);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_SameBytesTwice_UsesDifferentOpaqueObjectKeys()
    {
        byte[] payload = "duplicate conceptual file"u8.ToArray();
        using var firstInput = new MemoryStream(payload, writable: false);
        using var secondInput = new MemoryStream(payload, writable: false);

        LegalDocumentStorageObjectKey firstKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocumentStorageObjectKey secondKey =
            LegalDocumentStorageObjectKey.CreateNew();

        await storage.StoreAsync(
            firstKey,
            firstInput,
            payload.LongLength,
            CancellationToken.None);
        await storage.StoreAsync(
            secondKey,
            secondInput,
            payload.LongLength,
            CancellationToken.None);

        try
        {
            Assert.NotEqual(firstKey, secondKey);
            AssertOpaqueObjectKey(firstKey);
            AssertOpaqueObjectKey(secondKey);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                firstKey,
                CancellationToken.None);
            await storage.DeleteIfExistsAsync(
                secondKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_ExistingObjectKey_DoesNotOverwriteExistingObject()
    {
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        byte[] originalPayload = "original private object"u8.ToArray();
        byte[] replacementPayload = "replacement must be rejected"u8.ToArray();

        using var originalInput =
            new MemoryStream(originalPayload, writable: false);
        using var replacementInput =
            new MemoryStream(replacementPayload, writable: false);

        await storage.StoreAsync(
            objectKey,
            originalInput,
            originalPayload.LongLength,
            CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<
                LegalDocumentStorageObjectKeyConflictException>(
                () => storage.StoreAsync(
                    objectKey,
                    replacementInput,
                    replacementPayload.LongLength,
                    CancellationToken.None));

            await using ILegalDocumentStorageReadHandle handle =
                await storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None);
            using var copy = new MemoryStream();

            await handle.Content.CopyToAsync(
                copy,
                CancellationToken.None);

            Assert.Equal(originalPayload, copy.ToArray());
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteIfExists_ExistingThenMissing_RemainsIdempotent()
    {
        byte[] payload = "delete compensation test"u8.ToArray();
        using var input = new MemoryStream(payload, writable: false);

        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();

        await storage.StoreAsync(
            objectKey,
            input,
            payload.LongLength,
            CancellationToken.None);

        await storage.DeleteIfExistsAsync(
            objectKey,
            CancellationToken.None);
        await storage.DeleteIfExistsAsync(
            objectKey,
            CancellationToken.None);

        LegalDocumentStorageException exception =
            await Assert.ThrowsAnyAsync<LegalDocumentStorageException>(
                () => storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None));

        Assert.True(
            exception is LegalDocumentStorageObjectNotFoundException
                or LegalDocumentStorageUnavailableException);
    }

    [Fact]
    public async Task OpenRead_MissingOpaqueKey_ReturnsProviderNeutralStorageFailure()
    {
        LegalDocumentStorageObjectKey missingKey =
            LegalDocumentStorageObjectKey.CreateNew();

        LegalDocumentStorageException exception =
            await Assert.ThrowsAnyAsync<LegalDocumentStorageException>(
                () => storage.OpenReadAsync(
                    missingKey,
                    CancellationToken.None));

        Assert.True(
            exception is LegalDocumentStorageObjectNotFoundException
                or LegalDocumentStorageUnavailableException);
        Assert.DoesNotContain(
            environment.ServiceUrl,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            environment.AppAccessKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            environment.AppSecretKey,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredObject_AnonymousHttpRead_IsDenied()
    {
        byte[] payload = "private object"u8.ToArray();
        using var input = new MemoryStream(payload, writable: false);

        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();

        await storage.StoreAsync(
            objectKey,
            input,
            payload.LongLength,
            CancellationToken.None);

        try
        {
            using var httpClient = new HttpClient();
            using HttpResponseMessage response = await httpClient.GetAsync(
                $"{environment.ServiceUrl}/{DocumentStorageIntegrationEnvironment.BucketName}/{objectKey.Value}",
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ApplicationCredentials_CannotListBucket()
    {
        var request = new ListObjectsV2Request
        {
            BucketName = DocumentStorageIntegrationEnvironment.BucketName,
            MaxKeys = 1
        };

        AmazonS3Exception exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => applicationClient.ListObjectsV2Async(
                request,
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task ApplicationCredentials_CannotCreateBucket()
    {
        string unauthorizedBucketName =
            $"enma-denied-{Guid.NewGuid():N}";

        AmazonS3Exception exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => applicationClient.PutBucketAsync(
                new PutBucketRequest
                {
                    BucketName = unauthorizedBucketName
                },
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task Store_PreCanceledRequest_PropagatesCancellation()
    {
        byte[] payload = "cancelled object"u8.ToArray();
        using var input = new MemoryStream(payload, writable: false);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.StoreAsync(
                LegalDocumentStorageObjectKey.CreateNew(),
                input,
                payload.LongLength,
                cancellationTokenSource.Token));

        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task OpenRead_PreCanceledRequest_PropagatesCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.OpenReadAsync(
                LegalDocumentStorageObjectKey.CreateNew(),
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ReadHandle_Dispose_ReleasesResponseStreamExactlyOnce()
    {
        var responseStream = new TrackingMemoryStream([1, 2, 3]);
        var response = new GetObjectResponse
        {
            ResponseStream = responseStream
        };
        var handle = new S3LegalDocumentStorageReadHandle(response);

        Assert.Same(responseStream, handle.Content);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.True(responseStream.IsDisposed);
        Assert.Equal(1, responseStream.DisposeCallCount);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Content);
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        applicationClient.Dispose();
        return Task.CompletedTask;
    }

    private static void AssertOpaqueObjectKey(
        LegalDocumentStorageObjectKey objectKey)
    {
        Assert.Equal(
            LegalDocumentStorageObjectKey.EncodedLength,
            objectKey.Value.Length);
        Assert.All(
            objectKey.Value,
            character => Assert.True(
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'));
    }

    private sealed class TrackingMemoryStream(byte[] content)
        : MemoryStream(content, writable: false)
    {
        public int DisposeCallCount { get; private set; }

        public bool IsDisposed => DisposeCallCount > 0;

        protected override void Dispose(bool disposing)
        {
            DisposeCallCount++;
            base.Dispose(disposing);
        }
    }
}
