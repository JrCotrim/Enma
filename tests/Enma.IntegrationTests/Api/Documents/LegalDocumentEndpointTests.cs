using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Enma.Api.Contracts.Documents;
using Enma.Application.Authentication;
using Enma.Application.Documents.Storage;
using Enma.Domain.Authentication;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash =
        "synthetic-legal-document-http-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        20,
        19,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly RecordingLegalDocumentStorage storage = new();
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LegalDocumentEndpointTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.RemoveAll<ILegalDocumentStorage>();
            services.AddSingleton<ILegalDocumentStorage>(storage);
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
    }

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public void LegalDocumentContracts_ExposeMetadataWithoutStorageLocator()
    {
        Assert.Equal(
            [
                nameof(LegalDocumentMetadataResponse.Id),
                nameof(LegalDocumentMetadataResponse.ClientId),
                nameof(LegalDocumentMetadataResponse.ProcessId),
                nameof(LegalDocumentMetadataResponse.OriginalFileName),
                nameof(LegalDocumentMetadataResponse.ContentType),
                nameof(LegalDocumentMetadataResponse.SizeBytes),
                nameof(LegalDocumentMetadataResponse.ContentHashSha256),
                nameof(LegalDocumentMetadataResponse.UploadedByMembershipId),
                nameof(LegalDocumentMetadataResponse.CreatedAt)
            ],
            GetPropertyNames<LegalDocumentMetadataResponse>());
        Assert.Equal(
            [
                nameof(ListLegalDocumentsResponse.Items),
                nameof(ListLegalDocumentsResponse.PageNumber),
                nameof(ListLegalDocumentsResponse.PageSize),
                nameof(ListLegalDocumentsResponse.HasNext)
            ],
            GetPropertyNames<ListLegalDocumentsResponse>());
        Assert.DoesNotContain(
            "StoredObjectKey",
            GetPropertyNames<LegalDocumentMetadataResponse>());
    }

    [Fact]
    public async Task LegalDocumentEndpoints_AnonymousAndForeignOrganizationAccess_AreDeniedBeforeStorage()
    {
        Guid anonymousOrganizationId = Guid.NewGuid();
        Guid anonymousDocumentId = Guid.NewGuid();
        using HttpResponseMessage anonymousList = await client.GetAsync(
            GetDocumentsPath(anonymousOrganizationId));
        using HttpResponseMessage anonymousDetail = await client.GetAsync(
            GetDocumentPath(anonymousOrganizationId, anonymousDocumentId));
        using HttpResponseMessage anonymousDownload = await client.GetAsync(
            GetDocumentContentPath(
                anonymousOrganizationId,
                anonymousDocumentId));

        await AssertEmptyNoStoreAsync(
            anonymousList,
            HttpStatusCode.Unauthorized);
        await AssertEmptyNoStoreAsync(
            anonymousDetail,
            HttpStatusCode.Unauthorized);
        await AssertEmptyNoStoreAsync(
            anonymousDownload,
            HttpStatusCode.Unauthorized);

        User actor = CreateUser("foreign-route");
        Organization allowedOrganization = CreateOrganization("Allowed route");
        Organization foreignOrganization = CreateOrganization("Foreign route");
        OrganizationMembership membership = CreateMembership(
            actor,
            allowedOrganization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [allowedOrganization, foreignOrganization],
            [membership],
            [],
            [],
            []);

        using HttpResponseMessage foreignList = await SendGetAsync(
            GetDocumentsPath(foreignOrganization.Id),
            rawHandle);
        using HttpResponseMessage foreignDetail = await SendGetAsync(
            GetDocumentPath(foreignOrganization.Id, Guid.NewGuid()),
            rawHandle);
        using HttpResponseMessage foreignDownload = await SendGetAsync(
            GetDocumentContentPath(
                foreignOrganization.Id,
                Guid.NewGuid()),
            rawHandle);

        await AssertEmptyNoStoreAsync(foreignList, HttpStatusCode.Forbidden);
        await AssertEmptyNoStoreAsync(foreignDetail, HttpStatusCode.Forbidden);
        await AssertEmptyNoStoreAsync(
            foreignDownload,
            HttpStatusCode.Forbidden);

        User inactiveActor = CreateUser("inactive-list");
        inactiveActor.Deactivate();
        Organization inactiveOrganization = CreateOrganization(
            "Inactive list");
        OrganizationMembership inactiveMembership = CreateMembership(
            inactiveActor,
            inactiveOrganization,
            OrganizationRole.Member);
        string inactiveRawHandle = await SeedAuthenticatedUserAsync(
            inactiveActor,
            [],
            [inactiveOrganization],
            [inactiveMembership],
            [],
            [],
            []);
        using HttpResponseMessage inactiveList = await SendGetAsync(
            GetDocumentsPath(inactiveOrganization.Id),
            inactiveRawHandle);

        await AssertEmptyNoStoreAsync(
            inactiveList,
            HttpStatusCode.Unauthorized);
        Assert.Equal(0, storage.OpenReadCount);
    }

    [Fact]
    public async Task ListLegalDocuments_SameTenant_BindsApprovedFiltersPaginationAndReturnsPrivateMetadata()
    {
        User actor = CreateUser("list");
        Organization organization = CreateOrganization("List");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        ClientEntity clientA = CreateClient(organization, "Client Alpha");
        ClientEntity clientB = CreateClient(organization, "Client Beta");
        LegalProcess process = CreateProcess(
            organization,
            clientA,
            "Process Alpha");
        LegalDocument directClientDocument = CreateDocument(
            organization,
            membership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "client-alpha.pdf",
            "direct client"u8.ToArray(),
            clientId: clientA.Id,
            createdMinutesAgo: 3);
        LegalDocument processDocument = CreateDocument(
            organization,
            membership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "petition-final.pdf",
            "process document"u8.ToArray(),
            processId: process.Id,
            createdMinutesAgo: 2);
        LegalDocument otherClientDocument = CreateDocument(
            organization,
            membership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "client-beta.pdf",
            "other client"u8.ToArray(),
            clientId: clientB.Id,
            createdMinutesAgo: 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [clientA, clientB],
            [process],
            [directClientDocument, processDocument, otherClientDocument]);

        using HttpResponseMessage defaultsResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);
        Assert.True(defaultsResponse.Headers.CacheControl?.NoStore);
        string defaultsJson = await defaultsResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(
            "storedObjectKey",
            defaultsJson,
            StringComparison.OrdinalIgnoreCase);
        ListLegalDocumentsResponse defaults =
            Assert.IsType<ListLegalDocumentsResponse>(
                JsonSerializer.Deserialize<ListLegalDocumentsResponse>(
                    defaultsJson,
                    JsonSerializerOptions.Web));
        Assert.Equal(1, defaults.PageNumber);
        Assert.Equal(20, defaults.PageSize);
        Assert.False(defaults.HasNext);
        Assert.Equal(3, defaults.Items.Count);
        Assert.All(
            defaults.Items,
            item => Assert.Equal(64, item.ContentHashSha256.Length));

        using HttpResponseMessage searchResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id) +
                "?search=petition&page=1&pageSize=1",
            rawHandle);
        ListLegalDocumentsResponse search = Assert.IsType<
            ListLegalDocumentsResponse>(
                await searchResponse.Content
                    .ReadFromJsonAsync<ListLegalDocumentsResponse>());
        Assert.Equal(processDocument.Id, Assert.Single(search.Items).Id);
        Assert.Equal(1, search.PageNumber);
        Assert.Equal(1, search.PageSize);

        using HttpResponseMessage pageResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id) + "?page=2&pageSize=1",
            rawHandle);
        ListLegalDocumentsResponse page = Assert.IsType<
            ListLegalDocumentsResponse>(
                await pageResponse.Content
                    .ReadFromJsonAsync<ListLegalDocumentsResponse>());
        Assert.Equal(processDocument.Id, Assert.Single(page.Items).Id);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(1, page.PageSize);
        Assert.True(page.HasNext);

        using HttpResponseMessage clientResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id) +
                $"?clientId={clientA.Id:D}",
            rawHandle);
        ListLegalDocumentsResponse clientResult = Assert.IsType<
            ListLegalDocumentsResponse>(
                await clientResponse.Content
                    .ReadFromJsonAsync<ListLegalDocumentsResponse>());
        Assert.Equal(
            [processDocument.Id, directClientDocument.Id],
            clientResult.Items.Select(item => item.Id).ToArray());

        using HttpResponseMessage processResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id) +
                $"?processId={process.Id:D}",
            rawHandle);
        ListLegalDocumentsResponse processResult = Assert.IsType<
            ListLegalDocumentsResponse>(
                await processResponse.Content
                    .ReadFromJsonAsync<ListLegalDocumentsResponse>());
        Assert.Equal(processDocument.Id, Assert.Single(processResult.Items).Id);

        using HttpResponseMessage invalidResponse = await SendGetAsync(
            GetDocumentsPath(organization.Id) +
                $"?clientId={clientA.Id:D}&processId={process.Id:D}",
            rawHandle);
        await AssertEmptyNoStoreAsync(
            invalidResponse,
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLegalDocument_SameTenantSucceeds_WhileMissingAndForeignAreIndistinguishable()
    {
        User actor = CreateUser("detail-actor");
        User foreignUploader = CreateUser("detail-foreign-uploader");
        Organization organization = CreateOrganization("Detail Alpha");
        Organization foreignOrganization = CreateOrganization("Detail Beta");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        OrganizationMembership foreignMembership = CreateMembership(
            foreignUploader,
            foreignOrganization,
            OrganizationRole.Owner);
        LegalDocument document = CreateDocument(
            organization,
            membership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "detail.pdf",
            "detail"u8.ToArray());
        LegalDocument foreignDocument = CreateDocument(
            foreignOrganization,
            foreignMembership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "foreign.pdf",
            "foreign"u8.ToArray());
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [foreignUploader],
            [organization, foreignOrganization],
            [membership, foreignMembership],
            [],
            [],
            [document, foreignDocument]);

        using HttpResponseMessage success = await SendGetAsync(
            GetDocumentPath(organization.Id, document.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.True(success.Headers.CacheControl?.NoStore);
        string json = await success.Content.ReadAsStringAsync();
        Assert.DoesNotContain(
            document.StoredObjectKey,
            json,
            StringComparison.Ordinal);
        LegalDocumentMetadataResponse metadata = Assert.IsType<
            LegalDocumentMetadataResponse>(
                JsonSerializer.Deserialize<LegalDocumentMetadataResponse>(
                    json,
                    JsonSerializerOptions.Web));
        Assert.Equal(document.Id, metadata.Id);
        Assert.Equal(document.OriginalFileName, metadata.OriginalFileName);
        Assert.Equal(document.SizeBytes, metadata.SizeBytes);

        using HttpResponseMessage missing = await SendGetAsync(
            GetDocumentPath(organization.Id, Guid.NewGuid()),
            rawHandle);
        using HttpResponseMessage foreign = await SendGetAsync(
            GetDocumentPath(organization.Id, foreignDocument.Id),
            rawHandle);

        await AssertEmptyNoStoreAsync(missing, HttpStatusCode.NotFound);
        await AssertEmptyNoStoreAsync(foreign, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DownloadLegalDocument_AuthorizedRequest_StreamsExactPrivateAttachmentAndDisposesHandle()
    {
        byte[] payload = Enumerable.Range(0, 180_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        const string fileName = "petição \"final\" (réu); #1.pdf";
        User actor = CreateUser("download");
        Organization organization = CreateOrganization("Download");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument document = CreateDocument(
            organization,
            membership,
            objectKey,
            "download-placeholder.pdf",
            payload);
        storage.AddContent(objectKey, payload);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            [document]);
        await SetPersistedFileNameAsync(document.Id, fileName);

        using HttpResponseMessage response = await SendGetAsync(
            GetDocumentContentPath(organization.Id, document.Id),
            rawHandle);
        byte[] actual = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(payload, actual);
        Assert.Equal(
            document.ContentType,
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload.LongLength, response.Content.Headers.ContentLength);
        Assert.Equal(
            "attachment",
            response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(
            fileName,
            response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.ETag);
        Assert.DoesNotContain("bytes", response.Headers.AcceptRanges);
        TrackingStorageReadHandle handle = Assert.IsType<
            TrackingStorageReadHandle>(storage.LastHandle);
        var content = Assert.IsType<ChunkedReadStream>(handle.Content);
        Assert.True(content.ReadCount > 1);
        Assert.True(handle.IsDisposed);
    }

    [Fact]
    public async Task DownloadLegalDocument_MissingForeignDeniedAndUnavailable_AreSafeAndDoNotExposeStorage()
    {
        User actor = CreateUser("download-safe-actor");
        User foreignUploader = CreateUser("download-safe-foreign");
        Organization organization = CreateOrganization("Download safe Alpha");
        Organization foreignOrganization = CreateOrganization(
            "Download safe Beta");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        OrganizationMembership foreignMembership = CreateMembership(
            foreignUploader,
            foreignOrganization,
            OrganizationRole.Owner);
        LegalDocumentStorageObjectKey unavailableKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument unavailableDocument = CreateDocument(
            organization,
            membership,
            unavailableKey,
            "unavailable.pdf",
            "metadata only"u8.ToArray());
        LegalDocument foreignDocument = CreateDocument(
            foreignOrganization,
            foreignMembership,
            LegalDocumentStorageObjectKey.CreateNew(),
            "foreign.pdf",
            "foreign"u8.ToArray());
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [foreignUploader],
            [organization, foreignOrganization],
            [membership, foreignMembership],
            [],
            [],
            [unavailableDocument, foreignDocument]);

        using HttpResponseMessage missing = await SendGetAsync(
            GetDocumentContentPath(organization.Id, Guid.NewGuid()),
            rawHandle);
        using HttpResponseMessage foreign = await SendGetAsync(
            GetDocumentContentPath(organization.Id, foreignDocument.Id),
            rawHandle);
        await AssertEmptyNoStoreAsync(missing, HttpStatusCode.NotFound);
        await AssertEmptyNoStoreAsync(foreign, HttpStatusCode.NotFound);
        Assert.Equal(0, storage.OpenReadCount);

        using HttpResponseMessage unavailable = await SendGetAsync(
            GetDocumentContentPath(
                organization.Id,
                unavailableDocument.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
        string unavailableBody = await unavailable.Content.ReadAsStringAsync();
        Assert.Contains("Document content unavailable", unavailableBody);
        Assert.DoesNotContain(
            unavailableKey.Value,
            unavailableBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bucket",
            unavailableBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "storage endpoint",
            unavailableBody,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, storage.OpenReadCount);

        User inactiveActor = CreateUser("download-inactive");
        inactiveActor.Deactivate();
        Organization inactiveOrganization = CreateOrganization(
            "Download inactive");
        OrganizationMembership inactiveMembership = CreateMembership(
            inactiveActor,
            inactiveOrganization,
            OrganizationRole.Owner);
        string inactiveRawHandle = await SeedAuthenticatedUserAsync(
            inactiveActor,
            [],
            [inactiveOrganization],
            [inactiveMembership],
            [],
            [],
            []);

        using HttpResponseMessage inactive = await SendGetAsync(
            GetDocumentContentPath(inactiveOrganization.Id, Guid.NewGuid()),
            inactiveRawHandle);
        await AssertEmptyNoStoreAsync(inactive, HttpStatusCode.Unauthorized);
        Assert.Equal(1, storage.OpenReadCount);
    }

    [Fact]
    public async Task DownloadLegalDocument_ClientCancellation_ReachesStreamAndDisposesHandle()
    {
        User actor = CreateUser("cancel");
        Organization organization = CreateOrganization("Cancel");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.CreateNew();
        LegalDocument document = CreateDocument(
            organization,
            membership,
            objectKey,
            "cancel.pdf",
            [0x01, 0x02]);
        var cancellationStream = new CancellationObservingStream();
        storage.AddContent(objectKey, cancellationStream, document.SizeBytes);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            [document]);
        using var cancellation = new CancellationTokenSource();
        using var request = CreateAuthenticatedGetRequest(
            GetDocumentContentPath(organization.Id, document.Id),
            rawHandle);

        Task<HttpResponseMessage> requestCompletion = client.SendAsync(
            request,
            cancellation.Token);
        await cancellationStream.SecondReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await requestCompletion);
        TrackingStorageReadHandle handle = Assert.IsType<
            TrackingStorageReadHandle>(storage.LastHandle);
        await handle.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancellationStream.CancellationObserved);
        Assert.True(handle.IsDisposed);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties().Select(property => property.Name).ToArray();
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Document HTTP {marker}",
            $"document-http-{marker}-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        return user;
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
    }

    private static OrganizationMembership CreateMembership(
        User user,
        Organization organization,
        OrganizationRole role)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1));
    }

    private static ClientEntity CreateClient(
        Organization organization,
        string name)
    {
        return new ClientEntity(organization.Id, name, Now.AddMinutes(-10));
    }

    private static LegalProcess CreateProcess(
        Organization organization,
        ClientEntity client,
        string title)
    {
        return new LegalProcess(
            organization.Id,
            client.Id,
            title,
            Now.AddMinutes(-9));
    }

    private static LegalDocument CreateDocument(
        Organization organization,
        OrganizationMembership uploader,
        LegalDocumentStorageObjectKey objectKey,
        string originalFileName,
        byte[] payload,
        Guid? clientId = null,
        Guid? processId = null,
        int createdMinutesAgo = 1)
    {
        return new LegalDocument(
            organization.Id,
            clientId,
            processId,
            originalFileName,
            objectKey.Value,
            "application/pdf",
            payload.LongLength,
            new LegalDocumentContentHash(SHA256.HashData(payload)),
            uploader.Id,
            Now.AddMinutes(-createdMinutesAgo));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User actor,
        IReadOnlyCollection<User> otherUsers,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<ClientEntity> clients,
        IReadOnlyCollection<LegalProcess> processes,
        IReadOnlyCollection<LegalDocument> documents)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            actor.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            actor.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.Add(actor);
        dbContext.Users.AddRange(otherUsers);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.Clients.AddRange(clients);
        dbContext.LegalProcesses.AddRange(processes);
        dbContext.LegalDocuments.AddRange(documents);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        string path,
        string rawHandle)
    {
        using HttpRequestMessage request = CreateAuthenticatedGetRequest(
            path,
            rawHandle);
        return await client.SendAsync(request);
    }

    private async Task SetPersistedFileNameAsync(
        Guid documentId,
        string fileName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE legal_documents SET original_file_name = {fileName} WHERE id = {documentId}");
    }

    private static HttpRequestMessage CreateAuthenticatedGetRequest(
        string path,
        string rawHandle)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return request;
    }

    private static async Task AssertEmptyNoStoreAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
    }

    private static string GetDocumentsPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/documents";
    }

    private static string GetDocumentPath(
        Guid organizationId,
        Guid documentId)
    {
        return $"{GetDocumentsPath(organizationId)}/{documentId:D}";
    }

    private static string GetDocumentContentPath(
        Guid organizationId,
        Guid documentId)
    {
        return $"{GetDocumentPath(organizationId, documentId)}/content";
    }

    private sealed class RecordingLegalDocumentStorage : ILegalDocumentStorage
    {
        private readonly ConcurrentDictionary<
            string,
            Func<TrackingStorageReadHandle>> contentFactories = new(
                StringComparer.Ordinal);
        private int openReadCount;

        public int OpenReadCount => Volatile.Read(ref openReadCount);

        public ILegalDocumentStorageReadHandle? LastHandle { get; private set; }

        public void AddContent(
            LegalDocumentStorageObjectKey objectKey,
            byte[] content)
        {
            ArgumentNullException.ThrowIfNull(content);
            byte[] privateCopy = (byte[])content.Clone();
            AddContent(
                objectKey,
                () => new ChunkedReadStream(privateCopy, 4_096),
                privateCopy.LongLength);
        }

        public void AddContent(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength)
        {
            ArgumentNullException.ThrowIfNull(content);
            AddContent(objectKey, () => content, contentLength);
        }

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!contentFactories.TryGetValue(
                    objectKey.Value,
                    out Func<TrackingStorageReadHandle>? factory))
            {
                Interlocked.Increment(ref openReadCount);
                throw new LegalDocumentStorageObjectNotFoundException();
            }

            TrackingStorageReadHandle handle = factory();
            LastHandle = handle;
            Interlocked.Increment(ref openReadCount);
            return Task.FromResult<ILegalDocumentStorageReadHandle>(handle);
        }

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private void AddContent(
            LegalDocumentStorageObjectKey objectKey,
            Func<Stream> contentFactory,
            long contentLength)
        {
            contentFactories[objectKey.Value] = () =>
                new TrackingStorageReadHandle(
                    contentFactory(),
                    contentLength);
        }
    }

    private sealed class TrackingStorageReadHandle(
        Stream content,
        long contentLength) : ILegalDocumentStorageReadHandle
    {
        private int disposed;

        public Stream Content { get; } = content;

        public long ContentLength { get; } = contentLength;

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await Content.DisposeAsync();
            Disposed.TrySetResult();
        }
    }

    private sealed class ChunkedReadStream(
        byte[] content,
        int maximumChunkSize) : Stream
    {
        private int position;
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.LongLength;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .AsTask();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private int ReadCore(Span<byte> buffer)
        {
            Interlocked.Increment(ref readCount);
            int remaining = content.Length - position;
            int bytesToCopy = Math.Min(
                Math.Min(remaining, buffer.Length),
                maximumChunkSize);

            if (bytesToCopy <= 0)
            {
                return 0;
            }

            content.AsSpan(position, bytesToCopy).CopyTo(buffer);
            position += bytesToCopy;
            return bytesToCopy;
        }
    }

    private sealed class CancellationObservingStream : Stream
    {
        private int readCount;

        public bool CancellationObserved { get; private set; }

        public TaskCompletionSource SecondReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 2;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                buffer.Span[0] = 0x01;
                return 1;
            }

            SecondReadStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .AsTask();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
