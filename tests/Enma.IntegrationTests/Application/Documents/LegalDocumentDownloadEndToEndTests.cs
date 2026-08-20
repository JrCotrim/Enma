using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Enma.Application.Authorization;
using Enma.Application.Documents.Download;
using Enma.Application.Documents.Storage;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Documents;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Application.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentDownloadEndToEndTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        20,
        17,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly AmazonS3Client applicationClient;
    private readonly S3LegalDocumentStorage storage;

    public LegalDocumentDownloadEndToEndTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        DocumentStorageIntegrationEnvironment environment =
            DocumentStorageIntegrationEnvironment.Load();
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

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        applicationClient.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_WithRealPostgreSqlAndMinio_StreamsExactTrustedContentWithoutMutation()
    {
        byte[] payload = Enumerable.Range(0, 16_385)
            .Select(index => (byte)(index % 251))
            .ToArray();
        AccessGraph graph = CreateGraph(
            "Download Success",
            "document-download-success",
            OrganizationRole.Member);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument document = CreateDocument(
            graph,
            objectKey,
            "trusted-original.pdf",
            "application/pdf",
            payload);
        using var input = new MemoryStream(payload, writable: false);

        await storage.StoreAsync(
            objectKey,
            input,
            payload.LongLength,
            CancellationToken.None);

        LegalDocumentDownload? download = null;

        try
        {
            await SeedAsync(graph.Entities.Append(document).ToArray());
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            DownloadLegalDocumentUseCase useCase = CreateUseCase(
                dbContext,
                storage);

            DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
                new DownloadLegalDocumentQuery(
                    graph.User.Id,
                    graph.Organization.Id,
                    document.Id));

            Assert.Equal(
                DownloadLegalDocumentResultStatus.Succeeded,
                result.Status);
            download = Assert.IsType<LegalDocumentDownload>(result.Download);
            Assert.Equal(document.Id, download.DocumentId);
            Assert.Equal(document.OriginalFileName, download.OriginalFileName);
            Assert.Equal(document.ContentType, download.ContentType);
            Assert.Equal(document.SizeBytes, download.SizeBytes);

            var actual = new List<byte>(payload.Length);
            byte[] buffer = new byte[1_024];
            int readCount = 0;
            int bytesRead;

            while ((bytesRead = await download.Content.ReadAsync(
                       buffer.AsMemory(),
                       CancellationToken.None)) > 0)
            {
                readCount++;
                actual.AddRange(buffer.AsSpan(0, bytesRead).ToArray());
            }

            Assert.True(readCount > 1);
            Assert.Equal(payload, actual);

            await download.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => _ = download.Content);

            await using ILegalDocumentStorageReadHandle verificationRead =
                await storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None);
            using var verificationCopy = new MemoryStream();
            await verificationRead.Content.CopyToAsync(
                verificationCopy,
                CancellationToken.None);

            Assert.Equal(payload, verificationCopy.ToArray());
            Assert.Equal(payload.LongLength, verificationRead.ContentLength);
        }
        finally
        {
            if (download is not null)
            {
                await download.DisposeAsync();
            }

            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistedObjectIsMissing_ReturnsConsistencyFailureWithoutMetadataMutation()
    {
        byte[] payload = "metadata remains authoritative"u8.ToArray();
        AccessGraph graph = CreateGraph(
            "Missing Object",
            "document-download-missing-object",
            OrganizationRole.Owner);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument document = CreateDocument(
            graph,
            objectKey,
            "missing.pdf",
            "application/pdf",
            payload);
        await SeedAsync(graph.Entities.Append(document).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        DownloadLegalDocumentUseCase useCase = CreateUseCase(
            dbContext,
            storage);

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            new DownloadLegalDocumentQuery(
                graph.User.Id,
                graph.Organization.Id,
                document.Id));

        Assert.Same(DownloadLegalDocumentResult.ContentUnavailable, result);
        LegalDocument persisted = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == document.Id);
        Assert.Equal(document.StoredObjectKey, persisted.StoredObjectKey);
        Assert.Equal(document.SizeBytes, persisted.SizeBytes);
    }

    [Fact]
    public async Task ExecuteAsync_MissingAndForeignDocumentRemainIndistinguishableBeforeStorage()
    {
        byte[] foreignPayload = "foreign tenant private content"u8.ToArray();
        AccessGraph graphA = CreateGraph(
            "Tenant Alpha",
            "document-download-tenant-alpha",
            OrganizationRole.Owner);
        AccessGraph graphB = CreateGraph(
            "Tenant Beta",
            "document-download-tenant-beta",
            OrganizationRole.Owner);
        LegalDocumentStorageObjectKey foreignObjectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument foreignDocument = CreateDocument(
            graphB,
            foreignObjectKey,
            "foreign.pdf",
            "application/pdf",
            foreignPayload);
        using var input = new MemoryStream(foreignPayload, writable: false);
        await storage.StoreAsync(
            foreignObjectKey,
            input,
            foreignPayload.LongLength,
            CancellationToken.None);

        try
        {
            await SeedAsync(
                graphA.Entities
                    .Concat(graphB.Entities)
                    .Append(foreignDocument)
                    .ToArray());
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            var countingStorage = new CountingStorage(storage);
            DownloadLegalDocumentUseCase useCase = CreateUseCase(
                dbContext,
                countingStorage);

            DownloadLegalDocumentResult missing =
                await useCase.ExecuteAsync(
                    new DownloadLegalDocumentQuery(
                        graphA.User.Id,
                        graphA.Organization.Id,
                        Guid.NewGuid()));
            DownloadLegalDocumentResult foreign =
                await useCase.ExecuteAsync(
                    new DownloadLegalDocumentQuery(
                        graphA.User.Id,
                        graphA.Organization.Id,
                        foreignDocument.Id));

            Assert.Same(DownloadLegalDocumentResult.NotFound, missing);
            Assert.Same(missing, foreign);
            Assert.Equal(0, countingStorage.OpenReadCallCount);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                foreignObjectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithInactiveUser_DeniesBeforePrivateStorageAccess()
    {
        byte[] payload = "inactive actor cannot read"u8.ToArray();
        AccessGraph graph = CreateGraph(
            "Inactive Download",
            "document-download-inactive",
            OrganizationRole.Owner);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument document = CreateDocument(
            graph,
            objectKey,
            "inactive.pdf",
            "application/pdf",
            payload);
        graph.User.Deactivate();
        using var input = new MemoryStream(payload, writable: false);
        await storage.StoreAsync(
            objectKey,
            input,
            payload.LongLength,
            CancellationToken.None);

        try
        {
            await SeedAsync(graph.Entities.Append(document).ToArray());
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            var countingStorage = new CountingStorage(storage);
            DownloadLegalDocumentUseCase useCase = CreateUseCase(
                dbContext,
                countingStorage);

            DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
                new DownloadLegalDocumentQuery(
                    graph.User.Id,
                    graph.Organization.Id,
                    document.Id));

            Assert.Same(DownloadLegalDocumentResult.AccessDenied, result);
            Assert.Equal(0, countingStorage.OpenReadCallCount);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    private static DownloadLegalDocumentUseCase CreateUseCase(
        EnmaDbContext dbContext,
        ILegalDocumentStorage documentStorage)
    {
        var authorization = new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
        return new DownloadLegalDocumentUseCase(
            authorization,
            new LegalDocumentContentReadQueries(dbContext),
            documentStorage);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static AccessGraph CreateGraph(
        string name,
        string slug,
        OrganizationRole role)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var user = new User(
            $"{name} user",
            $"{slug}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);
        return new AccessGraph(organization, user, membership);
    }

    private static LegalDocument CreateDocument(
        AccessGraph graph,
        LegalDocumentStorageObjectKey objectKey,
        string originalFileName,
        string contentType,
        byte[] content)
    {
        return new LegalDocument(
            graph.Organization.Id,
            null,
            null,
            originalFileName,
            objectKey.Value,
            contentType,
            content.LongLength,
            new LegalDocumentContentHash(SHA256.HashData(content)),
            graph.Membership.Id,
            CreatedAt);
    }

    private sealed record AccessGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership)
    {
        public object[] Entities => [Organization, User, Membership];
    }

    private sealed class CountingStorage(ILegalDocumentStorage inner)
        : ILegalDocumentStorage
    {
        public int OpenReadCallCount { get; private set; }

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default)
        {
            return inner.StoreAsync(
                objectKey,
                content,
                contentLength,
                cancellationToken);
        }

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            OpenReadCallCount++;
            return inner.OpenReadAsync(objectKey, cancellationToken);
        }

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            return inner.DeleteIfExistsAsync(objectKey, cancellationToken);
        }
    }
}
