using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Enma.Api.Contracts.Documents;
using Enma.Application.Authentication;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Domain.Authentication;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Documents.Staging;
using Enma.Infrastructure.Documents.Upload;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using HttpMediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
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
    private readonly RecordingContentStager stager = new();
    private readonly UploadPersistenceController persistenceController = new();
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
            services.RemoveAll<ILegalDocumentContentStager>();
            services.AddSingleton<ILegalDocumentContentStager>(stager);
            services.RemoveAll<ILegalDocumentUploadPersistence>();
            services.AddScoped<LegalDocumentUploadPersistence>();
            services.AddScoped<ILegalDocumentUploadPersistence>(
                serviceProvider => new ControllableUploadPersistence(
                    serviceProvider.GetRequiredService<
                        LegalDocumentUploadPersistence>(),
                    persistenceController));
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
                nameof(UploadLegalDocumentRequest.File),
                nameof(UploadLegalDocumentRequest.ClientId),
                nameof(UploadLegalDocumentRequest.ProcessId)
            ],
            GetPropertyNames<UploadLegalDocumentRequest>());
        Assert.Equal(
            [nameof(UploadLegalDocumentResponse.Id)],
            GetPropertyNames<UploadLegalDocumentResponse>());
        Assert.Equal(
            [
                nameof(LegalDocumentMetadataResponse.Id),
                nameof(LegalDocumentMetadataResponse.ClientId),
                nameof(LegalDocumentMetadataResponse.ProcessId),
                nameof(LegalDocumentMetadataResponse.OriginalFileName),
                nameof(LegalDocumentMetadataResponse.ContentType),
                nameof(LegalDocumentMetadataResponse.SizeBytes),
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
        Assert.DoesNotContain(
            "StoredObjectKey",
            GetPropertyNames<UploadLegalDocumentResponse>());
    }

    [Fact]
    public async Task UploadLegalDocument_RequiresAuthenticationAndValidAntiforgery()
    {
        byte[] payload = CreateValidPdf();
        Organization anonymousOrganization = CreateOrganization("Anonymous upload");

        using HttpResponseMessage anonymous = await SendUploadAsync(
            anonymousOrganization.Id,
            rawHandle: null,
            csrf: null,
            payload);

        await AssertEmptyNoStoreAsync(
            anonymous,
            HttpStatusCode.Unauthorized);

        User actor = CreateUser("upload-csrf");
        Organization organization = CreateOrganization("Upload CSRF");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            []);

        using HttpResponseMessage missing = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf: null,
            payload);
        await AssertEmptyNoStoreAsync(missing, HttpStatusCode.BadRequest);

        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        using HttpResponseMessage invalid = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            requestTokenOverride: "invalid-antiforgery-token");
        await AssertEmptyNoStoreAsync(invalid, HttpStatusCode.BadRequest);

        using HttpResponseMessage valid = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload);

        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.True(valid.Headers.CacheControl?.NoStore);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, persistenceController.ExecuteCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_IgnoresUntrustedAuthorityFieldsAndUsesRouteAndSession()
    {
        User actor = CreateUser("upload-authority");
        Organization organization = CreateOrganization("Upload authority");
        Organization foreignOrganization = CreateOrganization("Upload body foreign");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization, foreignOrganization],
            [membership],
            [],
            [],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] payload = CreateValidPdf();
        const string SuppliedObjectKey = "caller-controlled-storage-key";
        Dictionary<string, string> untrustedFields = new()
        {
            ["organizationId"] = foreignOrganization.Id.ToString("D"),
            ["userId"] = Guid.NewGuid().ToString("D"),
            ["membershipId"] = Guid.NewGuid().ToString("D"),
            ["role"] = "Owner",
            ["storedObjectKey"] = SuppliedObjectKey,
            ["contentHashSha256"] = "00",
            ["createdAt"] = DateTimeOffset.MinValue.ToString("O"),
            ["sizeBytes"] = "1"
        };

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            extraFields: untrustedFields);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        UploadLegalDocumentResponse created = Assert.IsType<
            UploadLegalDocumentResponse>(
                await response.Content
                    .ReadFromJsonAsync<UploadLegalDocumentResponse>());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument document = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == created.Id);

        Assert.Equal(organization.Id, document.OrganizationId);
        Assert.Equal(membership.Id, document.UploadedByMembershipId);
        Assert.NotEqual(SuppliedObjectKey, document.StoredObjectKey);
        Assert.Equal(payload.LongLength, document.SizeBytes);
        Assert.Equal(SHA256.HashData(payload), document.ContentHashSha256.ToArray());
        Assert.Equal(Now, document.CreatedAt);

        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(document.StoredObjectKey, responseBody);
        Assert.DoesNotContain("contentHashSha256", responseBody);
    }

    [Fact]
    public async Task UploadLegalDocument_GeneralClientAndProcess_PersistExactClassificationAndBytes()
    {
        User actor = CreateUser("upload-classification");
        Organization organization = CreateOrganization("Upload classification");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        ClientEntity clientEntity = CreateClient(organization, "Upload client");
        LegalProcess process = CreateProcess(
            organization,
            clientEntity,
            "Upload process");
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [clientEntity],
            [process],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] payload = CreateValidPdf();

        using HttpResponseMessage general = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            fileName: "general.pdf");
        using HttpResponseMessage directClient = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            fileName: "client.pdf",
            clientId: clientEntity.Id);
        using HttpResponseMessage processResponse = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            fileName: "process.pdf",
            processId: process.Id);

        UploadLegalDocumentResponse generalCreated =
            await AssertCreatedUploadAsync(general, organization.Id);
        UploadLegalDocumentResponse clientCreated =
            await AssertCreatedUploadAsync(directClient, organization.Id);
        UploadLegalDocumentResponse processCreated =
            await AssertCreatedUploadAsync(processResponse, organization.Id);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument[] documents = await dbContext.LegalDocuments
            .AsNoTracking()
            .Where(item =>
                item.Id == generalCreated.Id ||
                item.Id == clientCreated.Id ||
                item.Id == processCreated.Id)
            .ToArrayAsync();

        LegalDocument generalDocument = Assert.Single(
            documents,
            item => item.Id == generalCreated.Id);
        LegalDocument clientDocument = Assert.Single(
            documents,
            item => item.Id == clientCreated.Id);
        LegalDocument processDocument = Assert.Single(
            documents,
            item => item.Id == processCreated.Id);
        Assert.Null(generalDocument.ClientId);
        Assert.Null(generalDocument.ProcessId);
        Assert.Equal(clientEntity.Id, clientDocument.ClientId);
        Assert.Null(clientDocument.ProcessId);
        Assert.Null(processDocument.ClientId);
        Assert.Equal(process.Id, processDocument.ProcessId);

        foreach (LegalDocument document in documents)
        {
            Assert.Equal(
                payload,
                storage.GetStoredContent(
                    LegalDocumentStorageObjectKey.Parse(
                        document.StoredObjectKey)));
        }

        Assert.Equal(3, storage.StoreCallCount);
        Assert.True(stager.SourceWasReadableDuringStage);
        Assert.False(stager.SourceWasMemoryStream);
        Assert.True(stager.IsSourceDisposed);
    }

    [Fact]
    public async Task UploadLegalDocument_ForeignClientAndProcess_AreSafeNotFound()
    {
        User actor = CreateUser("upload-foreign-relation");
        User foreignOwner = CreateUser("upload-foreign-owner");
        Organization organization = CreateOrganization("Upload relation A");
        Organization foreignOrganization = CreateOrganization("Upload relation B");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        OrganizationMembership foreignMembership = CreateMembership(
            foreignOwner,
            foreignOrganization,
            OrganizationRole.Owner);
        ClientEntity foreignClient = CreateClient(
            foreignOrganization,
            "Foreign upload client");
        LegalProcess foreignProcess = CreateProcess(
            foreignOrganization,
            foreignClient,
            "Foreign upload process");
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [foreignOwner],
            [organization, foreignOrganization],
            [membership, foreignMembership],
            [foreignClient],
            [foreignProcess],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] payload = CreateValidPdf();

        using HttpResponseMessage clientResponse = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            clientId: foreignClient.Id);
        using HttpResponseMessage processResponse = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload,
            processId: foreignProcess.Id);

        await AssertEmptyNoStoreAsync(clientResponse, HttpStatusCode.NotFound);
        await AssertEmptyNoStoreAsync(processResponse, HttpStatusCode.NotFound);
        Assert.Equal(0, storage.StoreCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_InactiveSecurityState_IsRejectedBeforeStorage()
    {
        User inactiveUser = CreateUser("upload-inactive-user");
        inactiveUser.Deactivate();
        Organization userOrganization = CreateOrganization("Upload inactive user");
        OrganizationMembership userMembership = CreateMembership(
            inactiveUser,
            userOrganization,
            OrganizationRole.Member);
        string inactiveUserHandle = await SeedAuthenticatedUserAsync(
            inactiveUser,
            [],
            [userOrganization],
            [userMembership],
            [],
            [],
            []);

        User inactiveMember = CreateUser("upload-inactive-membership");
        Organization memberOrganization = CreateOrganization("Upload inactive member");
        OrganizationMembership inactiveMembership = CreateMembership(
            inactiveMember,
            memberOrganization,
            OrganizationRole.Member);
        inactiveMembership.Deactivate();
        string inactiveMembershipHandle = await SeedAuthenticatedUserAsync(
            inactiveMember,
            [],
            [memberOrganization],
            [inactiveMembership],
            [],
            [],
            []);

        User inactiveOrganizationUser = CreateUser("upload-inactive-organization");
        Organization inactiveOrganization = CreateOrganization(
            "Upload inactive organization");
        OrganizationMembership organizationMembership = CreateMembership(
            inactiveOrganizationUser,
            inactiveOrganization,
            OrganizationRole.Member);
        inactiveOrganization.Deactivate();
        string inactiveOrganizationHandle = await SeedAuthenticatedUserAsync(
            inactiveOrganizationUser,
            [],
            [inactiveOrganization],
            [organizationMembership],
            [],
            [],
            []);
        byte[] payload = CreateValidPdf();

        using HttpResponseMessage userResponse = await SendUploadAsync(
            userOrganization.Id,
            inactiveUserHandle,
            csrf: null,
            payload);
        using HttpResponseMessage membershipResponse = await SendUploadAsync(
            memberOrganization.Id,
            inactiveMembershipHandle,
            csrf: null,
            payload);
        using HttpResponseMessage organizationResponse = await SendUploadAsync(
            inactiveOrganization.Id,
            inactiveOrganizationHandle,
            csrf: null,
            payload);

        await AssertEmptyNoStoreAsync(userResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyNoStoreAsync(membershipResponse, HttpStatusCode.Forbidden);
        await AssertEmptyNoStoreAsync(organizationResponse, HttpStatusCode.Forbidden);
        Assert.Equal(0, storage.StoreCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_InvalidClassificationAndFileInputs_AreRejected()
    {
        User actor = CreateUser("upload-validation");
        Organization organization = CreateOrganization("Upload validation");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        ClientEntity clientEntity = CreateClient(organization, "Validation client");
        LegalProcess process = CreateProcess(
            organization,
            clientEntity,
            "Validation process");
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [clientEntity],
            [process],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] validPdf = CreateValidPdf();

        using HttpResponseMessage bothRelations = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            clientId: clientEntity.Id,
            processId: process.Id);
        using HttpResponseMessage empty = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            [],
            fileName: "empty.pdf");
        using HttpResponseMessage unsupported = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            fileName: "malware.exe");
        using HttpResponseMessage invalidStructure = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            "not a pdf"u8.ToArray(),
            fileName: "looks-valid.pdf");
        using HttpResponseMessage dangerousName = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            fileName: "evidence.exe.pdf");
        using HttpResponseMessage pathName = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            fileName: "../evidence.pdf");
        using HttpResponseMessage mismatchedType = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            fileName: "evidence.pdf",
            contentType: "image/png");
        using HttpResponseMessage structuralBypass = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            fileName: "fake.png",
            contentType: "image/png");
        using HttpResponseMessage missingFile = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            includeFile: false);
        using HttpResponseMessage multipleFiles = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            validPdf,
            includeSecondFile: true);

        foreach (HttpResponseMessage response in new[]
                 {
                     bothRelations,
                     empty,
                     unsupported,
                     invalidStructure,
                     dangerousName,
                     pathName,
                     mismatchedType,
                     structuralBypass,
                     missingFile,
                     multipleFiles
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        Assert.Equal(0, storage.StoreCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_ExactMaximumFileSize_IsAccepted()
    {
        (Organization organization, string rawHandle, CsrfPair csrf) =
            await SeedUploadActorAsync("exact-limit");
        byte[] maximumSizePdf = CreateMaximumSizeValidPdf();

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            maximumSizePdf,
            fileName: "maximum.pdf");

        UploadLegalDocumentResponse created =
            await AssertCreatedUploadAsync(response, organization.Id);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument document = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == created.Id);
        Assert.Equal(
            LegalDocumentUploadPolicy.MaximumFileSizeBytes,
            document.SizeBytes);
        Assert.Equal(
            maximumSizePdf,
            storage.GetStoredContent(
                LegalDocumentStorageObjectKey.Parse(
                    document.StoredObjectKey)));
    }

    [Fact]
    public async Task UploadLegalDocument_OverMaximumFileSize_IsRejectedBeforeUseCasePersistence()
    {
        User actor = CreateUser("upload-oversized");
        Organization organization = CreateOrganization("Upload oversized");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] oversized = new byte[(25 * 1024 * 1024) + 1];
        "%PDF-1.7"u8.CopyTo(oversized);

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            oversized);

        Assert.InRange((int)response.StatusCode, 400, 499);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, storage.StoreCallCount);
        Assert.Equal(0, persistenceController.ExecuteCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_ResourceRejectedAfterStorage_CompensatesObject()
    {
        User actor = CreateUser("upload-compensation");
        Organization organization = CreateOrganization("Upload compensation");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        ClientEntity clientEntity = CreateClient(organization, "Compensation client");
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [clientEntity],
            [],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        storage.AfterStoreAsync = async _ =>
        {
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            ClientEntity clientToDeactivate = await dbContext.Clients
                .SingleAsync(item => item.Id == clientEntity.Id);
            clientToDeactivate.Deactivate();
            await dbContext.SaveChangesAsync();
        };

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            CreateValidPdf(),
            clientId: clientEntity.Id);

        await AssertEmptyNoStoreAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.DeleteCallCount);
        Assert.Equal(0, storage.StoredObjectCount);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(0, await verificationContext.LegalDocuments.CountAsync());
    }

    [Fact]
    public async Task UploadLegalDocument_StorageUnavailable_MapsSafeServiceUnavailable()
    {
        (Organization organization, string rawHandle, CsrfPair csrf) =
            await SeedUploadActorAsync("storage-unavailable");
        storage.StoreException = new LegalDocumentStorageUnavailableException();

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            CreateValidPdf());

        await AssertSafeUploadUnavailableAsync(response);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.DeleteCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_ObjectKeyConflict_DoesNotDeletePreExistingObject()
    {
        (Organization organization, string rawHandle, CsrfPair csrf) =
            await SeedUploadActorAsync("key-conflict");
        storage.StoreException =
            new LegalDocumentStorageObjectKeyConflictException();

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            CreateValidPdf());

        await AssertSafeUploadUnavailableAsync(response);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(0, storage.DeleteCallCount);
    }

    [Fact]
    public async Task UploadLegalDocument_AmbiguousCommit_ReportsUnknownWithoutRetryOrDelete()
    {
        (Organization organization, string rawHandle, CsrfPair csrf) =
            await SeedUploadActorAsync("outcome-unknown");
        persistenceController.ThrowOutcomeUnknownAfterInner = true;

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            CreateValidPdf());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Document upload outcome unknown", body);
        Assert.Contains("may have succeeded", body);
        Assert.Contains("Do not retry automatically", body);
        Assert.DoesNotContain("bucket", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object key", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, persistenceController.ExecuteCallCount);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(0, storage.DeleteCallCount);
        Assert.Equal(1, storage.StoredObjectCount);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalDocuments.CountAsync());
    }

    [Fact]
    public async Task UploadLegalDocument_RequestCancellation_PropagatesAndDisposesInputStream()
    {
        (Organization organization, string rawHandle, CsrfPair csrf) =
            await SeedUploadActorAsync("cancellation");
        storage.BlockStoreUntilCancellation = true;
        using var cancellation = new CancellationTokenSource();

        Task<HttpResponseMessage> request = SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            CreateValidPdf(),
            cancellationToken: cancellation.Token);
        await storage.StoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await request);
        await storage.DeleteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(storage.StoreCancellationObserved);
        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.DeleteCallCount);
        Assert.True(stager.SourceWasReadableDuringStage);
        Assert.False(stager.SourceWasMemoryStream);
        Assert.True(stager.IsSourceDisposed);
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
        Assert.DoesNotContain(
            "contentHashSha256",
            defaultsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "uploadedByMembershipId",
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
        LegalDocumentMetadataResponse otherClientMetadata = Assert.Single(
            defaults.Items,
            item => item.Id == otherClientDocument.Id);
        Assert.Equal(
            otherClientDocument.ClientId,
            otherClientMetadata.ClientId);
        Assert.Equal(
            otherClientDocument.ProcessId,
            otherClientMetadata.ProcessId);
        Assert.Equal(
            otherClientDocument.OriginalFileName,
            otherClientMetadata.OriginalFileName);
        Assert.Equal(
            otherClientDocument.ContentType,
            otherClientMetadata.ContentType);
        Assert.Equal(otherClientDocument.SizeBytes, otherClientMetadata.SizeBytes);
        Assert.Equal(otherClientDocument.CreatedAt, otherClientMetadata.CreatedAt);

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
        Assert.DoesNotContain(
            "contentHashSha256",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "uploadedByMembershipId",
            json,
            StringComparison.OrdinalIgnoreCase);
        LegalDocumentMetadataResponse metadata = Assert.IsType<
            LegalDocumentMetadataResponse>(
                JsonSerializer.Deserialize<LegalDocumentMetadataResponse>(
                    json,
                    JsonSerializerOptions.Web));
        Assert.Equal(document.Id, metadata.Id);
        Assert.Equal(document.ClientId, metadata.ClientId);
        Assert.Equal(document.ProcessId, metadata.ProcessId);
        Assert.Equal(document.OriginalFileName, metadata.OriginalFileName);
        Assert.Equal(document.ContentType, metadata.ContentType);
        Assert.Equal(document.SizeBytes, metadata.SizeBytes);
        Assert.Equal(document.CreatedAt, metadata.CreatedAt);

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

    private async Task<(Organization Organization, string RawHandle, CsrfPair Csrf)>
        SeedUploadActorAsync(string marker)
    {
        User actor = CreateUser($"upload-{marker}");
        Organization organization = CreateOrganization($"Upload {marker}");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        return (organization, rawHandle, csrf);
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse result = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));

        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendUploadAsync(
        Guid organizationId,
        string? rawHandle,
        CsrfPair? csrf,
        byte[] payload,
        string fileName = "evidence.pdf",
        string contentType = "application/pdf",
        Guid? clientId = null,
        Guid? processId = null,
        IReadOnlyDictionary<string, string>? extraFields = null,
        bool includeFile = true,
        bool includeSecondFile = false,
        string? requestTokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetDocumentsPath(organizationId));
        AddCookiesAndCsrf(
            request,
            rawHandle,
            csrf,
            requestTokenOverride ?? csrf?.RequestToken);

        using var form = new MultipartFormDataContent(
            $"enma-{Guid.NewGuid():N}");
        if (includeFile)
        {
            var fileContent = new ByteArrayContent(payload);
            fileContent.Headers.ContentType =
                HttpMediaTypeHeaderValue.Parse(contentType);
            form.Add(fileContent, "file", fileName);
        }

        if (clientId is Guid contextualClientId)
        {
            form.Add(
                new StringContent(contextualClientId.ToString("D")),
                "clientId");
        }

        if (processId is Guid contextualProcessId)
        {
            form.Add(
                new StringContent(contextualProcessId.ToString("D")),
                "processId");
        }

        if (extraFields is not null)
        {
            foreach ((string name, string value) in extraFields)
            {
                form.Add(new StringContent(value), name);
            }
        }

        if (includeSecondFile)
        {
            var secondFile = new ByteArrayContent(CreateValidPdf());
            secondFile.Headers.ContentType =
                HttpMediaTypeHeaderValue.Parse("application/pdf");
            form.Add(secondFile, "otherFile", "second.pdf");
        }

        request.Content = form;
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AddCookiesAndCsrf(
        HttpRequestMessage request,
        string? rawHandle,
        CsrfPair? csrf,
        string? requestToken)
    {
        var cookies = new List<string>();

        if (rawHandle is not null)
        {
            cookies.Add($"{SessionCookieName}={rawHandle}");
        }

        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        if (cookies.Count > 0)
        {
            request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        }

        if (requestToken is not null)
        {
            request.Headers.Add(CsrfHeaderName, requestToken);
        }
    }

    private static IReadOnlyList<SetCookieHeaderValue> ParseSetCookies(
        HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                HeaderNames.SetCookie,
                out IEnumerable<string>? values))
        {
            return [];
        }

        return SetCookieHeaderValue.ParseList(values.ToList()).ToArray();
    }

    private static async Task<UploadLegalDocumentResponse>
        AssertCreatedUploadAsync(
            HttpResponseMessage response,
            Guid organizationId)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        UploadLegalDocumentResponse result = Assert.IsType<
            UploadLegalDocumentResponse>(
                await response.Content
                    .ReadFromJsonAsync<UploadLegalDocumentResponse>());
        Assert.Equal(
            GetDocumentPath(organizationId, result.Id),
            response.Headers.Location?.OriginalString);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storedObjectKey", body);
        Assert.DoesNotContain("bucket", body, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    private static async Task AssertSafeUploadUnavailableAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Document upload unavailable", body);
        Assert.DoesNotContain("bucket", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object key", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
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

    private static byte[] CreateValidPdf()
    {
        return "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n9\n%%EOF\n"u8.ToArray();
    }

    private static byte[] CreateMaximumSizeValidPdf()
    {
        byte[] content = new byte[
            LegalDocumentUploadPolicy.MaximumFileSizeBytes];
        content.AsSpan().Fill((byte)' ');
        "%PDF-1.7"u8.CopyTo(content);
        byte[] tail = "\nstartxref\n9\n%%EOF\n"u8.ToArray();
        tail.CopyTo(content, content.Length - tail.Length);
        return content;
    }

    private sealed class RecordingLegalDocumentStorage : ILegalDocumentStorage
    {
        private readonly ConcurrentDictionary<
            string,
            Func<TrackingStorageReadHandle>> contentFactories = new(
                StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> storedContent = new(
            StringComparer.Ordinal);
        private int openReadCount;
        private int storeCallCount;
        private int deleteCallCount;

        public int OpenReadCount => Volatile.Read(ref openReadCount);

        public int StoreCallCount => Volatile.Read(ref storeCallCount);

        public int DeleteCallCount => Volatile.Read(ref deleteCallCount);

        public int StoredObjectCount => storedContent.Count;

        public ILegalDocumentStorageReadHandle? LastHandle { get; private set; }

        public Exception? StoreException { get; set; }

        public Exception? DeleteException { get; set; }

        public Func<LegalDocumentStorageObjectKey, Task>? AfterStoreAsync
        {
            get;
            set;
        }

        public bool BlockStoreUntilCancellation { get; set; }

        public bool StoreCancellationObserved { get; private set; }

        public TaskCompletionSource StoreStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DeleteCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] GetStoredContent(LegalDocumentStorageObjectKey objectKey)
        {
            return (byte[])storedContent[objectKey.Value].Clone();
        }

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

        public async Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref storeCallCount);
            StoreStarted.TrySetResult();

            if (BlockStoreUntilCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    StoreCancellationObserved = true;
                    throw;
                }
            }

            if (StoreException is not null)
            {
                throw StoreException;
            }

            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            byte[] payload = copy.ToArray();

            if (payload.LongLength != contentLength)
            {
                throw new InvalidOperationException(
                    "The test storage received an unexpected content length.");
            }

            if (!storedContent.TryAdd(objectKey.Value, payload))
            {
                throw new LegalDocumentStorageObjectKeyConflictException();
            }

            AddContent(objectKey, payload);

            if (AfterStoreAsync is not null)
            {
                await AfterStoreAsync(objectKey);
            }
        }

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref deleteCallCount);

            try
            {
                if (DeleteException is not null)
                {
                    return Task.FromException(DeleteException);
                }

                storedContent.TryRemove(objectKey.Value, out _);
                contentFactories.TryRemove(objectKey.Value, out _);
                return Task.CompletedTask;
            }
            finally
            {
                DeleteCompleted.TrySetResult();
            }
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

    private sealed class RecordingContentStager : ILegalDocumentContentStager
    {
        private readonly TempFileLegalDocumentContentStager inner = new();
        private Stream? source;

        public bool SourceWasReadableDuringStage { get; private set; }

        public bool SourceWasMemoryStream { get; private set; }

        public bool IsSourceDisposed
        {
            get
            {
                if (source is null)
                {
                    return false;
                }

                try
                {
                    _ = source.ReadByte();
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public Task<ILegalDocumentStagedContent> StageAsync(
            Stream source,
            long declaredContentLength,
            CancellationToken cancellationToken = default)
        {
            this.source = source;
            SourceWasReadableDuringStage = source.CanRead;
            SourceWasMemoryStream = source is MemoryStream;

            return inner.StageAsync(
                source,
                declaredContentLength,
                cancellationToken);
        }
    }

    private sealed class UploadPersistenceController
    {
        private int executeCallCount;

        public bool ThrowOutcomeUnknownAfterInner { get; set; }

        public int ExecuteCallCount => Volatile.Read(ref executeCallCount);

        public void RecordExecute()
        {
            Interlocked.Increment(ref executeCallCount);
        }
    }

    private sealed class ControllableUploadPersistence(
        LegalDocumentUploadPersistence inner,
        UploadPersistenceController controller)
        : ILegalDocumentUploadPersistence
    {
        public async Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
            LegalDocumentUploadPersistenceRequest request,
            Stream content,
            Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
            CancellationToken cancellationToken = default)
        {
            controller.RecordExecute();

            LegalDocumentUploadPersistenceResult result =
                await inner.ExecuteAsync(
                    request,
                    content,
                    decide,
                    cancellationToken);

            if (controller.ThrowOutcomeUnknownAfterInner)
            {
                throw new LegalDocumentUploadOutcomeUnknownException();
            }

            return result;
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

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);
}
