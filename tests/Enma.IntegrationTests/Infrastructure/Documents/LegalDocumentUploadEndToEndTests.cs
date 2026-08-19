using System.Security.Cryptography;
using Enma.Application.Authorization;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Application.Processes;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentUploadEndToEndTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
        20,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset UploadedAt = new(
        2026,
        8,
        19,
        20,
        30,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_ValidPdf_AllAllowedRoles_PersistsExactMetadataAndPrivateObject(
        OrganizationRole role)
    {
        SeedGraph graph = await SeedGraphAsync(role: role);
        byte[] payload = CreateValidPdf();

        await using ServiceProvider serviceProvider = CreateServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        UploadLegalDocumentUseCase useCase = scope.ServiceProvider
            .GetRequiredService<UploadLegalDocumentUseCase>();
        ILegalDocumentStorage storage = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        using var input = new MemoryStream(payload, writable: false);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                null,
                null,
                "contract.pdf",
                "application/pdf",
                payload.LongLength,
                input));

        Assert.Equal(
            UploadLegalDocumentResultStatus.Succeeded,
            result.Status);
        Guid documentId = Assert.IsType<Guid>(result.DocumentId);
        Assert.True(input.CanRead);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument document = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == documentId);

        Assert.Equal(graph.Organization.Id, document.OrganizationId);
        Assert.Null(document.ClientId);
        Assert.Null(document.ProcessId);
        Assert.Equal("contract.pdf", document.OriginalFileName);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(payload.LongLength, document.SizeBytes);
        Assert.Equal(graph.Membership.Id, document.UploadedByMembershipId);
        Assert.Equal(UploadedAt, document.CreatedAt);
        Assert.Equal(
            SHA256.HashData(payload),
            document.ContentHashSha256.ToArray());

        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.Parse(document.StoredObjectKey);

        try
        {
            await using ILegalDocumentStorageReadHandle handle =
                await storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None);

            Assert.Equal(payload.LongLength, handle.ContentLength);

            using var copy = new MemoryStream();
            await handle.Content.CopyToAsync(
                copy,
                CancellationToken.None);

            Assert.Equal(payload, copy.ToArray());
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ClientBecomesInactiveAfterPrecheck_RevalidatesAndCompensatesObject()
    {
        SeedGraph graph = await SeedGraphAsync(includeClient: true);
        byte[] payload = CreateValidPdf();

        await using ServiceProvider serviceProvider = CreateServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        ILegalDocumentUploadPersistence innerPersistence = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentUploadPersistence>();
        ILegalDocumentStorage storage = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        var persistence = new BeforeExecuteUploadPersistence(
            innerPersistence,
            async _ =>
            {
                await using EnmaDbContext dbContext = fixture.CreateDbContext();
                Client client = await dbContext.Clients
                    .SingleAsync(item => item.Id == graph.Client!.Id);

                client.Deactivate();
                await dbContext.SaveChangesAsync();
            });

        UploadLegalDocumentUseCase useCase =
            CreateUseCase(scope.ServiceProvider, persistence);

        using var input = new MemoryStream(payload, writable: false);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                graph.Client!.Id,
                null,
                "client-contract.pdf",
                "application/pdf",
                payload.LongLength,
                input));

        Assert.Equal(
            UploadLegalDocumentResultStatus.RelatedClientUnavailable,
            result.Status);

        LegalDocumentStorageObjectKey objectKey =
            Assert.IsType<LegalDocumentStorageObjectKey>(
                persistence.LastObjectKey);

        await AssertNoDocumentsAsync();
        await AssertObjectMissingAsync(storage, objectKey);

        LegalDocumentUploadClientState lockedClient =
            Assert.IsType<LegalDocumentUploadClientState>(
                persistence.LastLockedState?.Client);
        Assert.False(lockedClient.IsActive);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(
            await verificationContext.Clients
                .Where(item => item.Id == graph.Client.Id)
                .Select(item => item.IsActive)
                .SingleAsync());
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("organization")]
    public async Task ExecuteAsync_ActorBecomesInactiveAfterAuthorization_RevalidatesAndCompensatesObject(
        string inactivePart)
    {
        SeedGraph graph = await SeedGraphAsync();
        byte[] payload = CreateValidPdf();

        await using ServiceProvider serviceProvider = CreateServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        ILegalDocumentUploadPersistence innerPersistence = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentUploadPersistence>();
        ILegalDocumentStorage storage = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        var persistence = new BeforeExecuteUploadPersistence(
            innerPersistence,
            async _ =>
            {
                await using EnmaDbContext dbContext = fixture.CreateDbContext();

                switch (inactivePart)
                {
                    case "membership":
                    {
                        OrganizationMembership membership =
                            await dbContext.OrganizationMemberships
                                .SingleAsync(
                                    item => item.Id == graph.Membership.Id);
                        membership.Deactivate();
                        break;
                    }
                    case "user":
                    {
                        User user = await dbContext.Users
                            .SingleAsync(item => item.Id == graph.User.Id);
                        user.Deactivate();
                        break;
                    }
                    case "organization":
                    {
                        Organization organization = await dbContext.Organizations
                            .SingleAsync(
                                item => item.Id == graph.Organization.Id);
                        organization.Deactivate();
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Unknown actor part '{inactivePart}'.");
                }

                await dbContext.SaveChangesAsync();
            });

        UploadLegalDocumentUseCase useCase =
            CreateUseCase(scope.ServiceProvider, persistence);

        using var input = new MemoryStream(payload, writable: false);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                null,
                null,
                $"{inactivePart}-race.pdf",
                "application/pdf",
                payload.LongLength,
                input));

        Assert.Equal(
            UploadLegalDocumentResultStatus.AccessDenied,
            result.Status);

        LegalDocumentStorageObjectKey objectKey =
            Assert.IsType<LegalDocumentStorageObjectKey>(
                persistence.LastObjectKey);

        LegalDocumentUploadActorState lockedActor =
            Assert.IsType<LegalDocumentUploadActorState>(
                persistence.LastLockedState?.Actor);

        Assert.Equal(inactivePart != "membership", lockedActor.IsMembershipActive);
        Assert.Equal(inactivePart != "user", lockedActor.IsUserActive);
        Assert.Equal(inactivePart != "organization", lockedActor.IsOrganizationActive);

        await AssertNoDocumentsAsync();
        await AssertObjectMissingAsync(storage, objectKey);
    }

    [Fact]
    public async Task ExecuteAsync_RoleChangesAfterAuthorization_RevalidatesUsingCurrentRoleAndKeepsObject()
    {
        SeedGraph graph = await SeedGraphAsync(role: OrganizationRole.Owner);
        byte[] payload = CreateValidPdf();

        await using ServiceProvider serviceProvider = CreateServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        ILegalDocumentUploadPersistence innerPersistence = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentUploadPersistence>();
        ILegalDocumentStorage storage = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        var persistence = new BeforeExecuteUploadPersistence(
            innerPersistence,
            async _ =>
            {
                await using EnmaDbContext dbContext = fixture.CreateDbContext();
                OrganizationMembership membership =
                    await dbContext.OrganizationMemberships
                        .SingleAsync(item => item.Id == graph.Membership.Id);

                membership.ChangeRole(OrganizationRole.Member);
                await dbContext.SaveChangesAsync();
            });

        UploadLegalDocumentUseCase useCase =
            CreateUseCase(scope.ServiceProvider, persistence);

        using var input = new MemoryStream(payload, writable: false);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                null,
                null,
                "role-race.pdf",
                "application/pdf",
                payload.LongLength,
                input));

        Assert.Equal(
            UploadLegalDocumentResultStatus.Succeeded,
            result.Status);
        Guid documentId = Assert.IsType<Guid>(result.DocumentId);

        LegalDocumentUploadActorState lockedActor =
            Assert.IsType<LegalDocumentUploadActorState>(
                persistence.LastLockedState?.Actor);
        Assert.Equal(OrganizationRole.Member, lockedActor.Role);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument document = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == documentId);

        Assert.Equal(
            OrganizationRole.Member,
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .Where(item => item.Id == graph.Membership.Id)
                .Select(item => item.Role)
                .SingleAsync());

        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.Parse(document.StoredObjectKey);

        try
        {
            await using ILegalDocumentStorageReadHandle handle =
                await storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None);

            using var copy = new MemoryStream();
            await handle.Content.CopyToAsync(copy, CancellationToken.None);
            Assert.Equal(payload, copy.ToArray());
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessRemovedAfterPrecheck_RevalidatesAndCompensatesObject()
    {
        SeedGraph graph = await SeedGraphAsync(
            includeClient: true,
            includeProcess: true);
        byte[] payload = CreateValidPdf();

        await using ServiceProvider serviceProvider = CreateServiceProvider();
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        ILegalDocumentUploadPersistence innerPersistence = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentUploadPersistence>();
        ILegalDocumentStorage storage = scope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        var persistence = new BeforeExecuteUploadPersistence(
            innerPersistence,
            async _ =>
            {
                await using EnmaDbContext dbContext = fixture.CreateDbContext();
                LegalProcess process = await dbContext.LegalProcesses
                    .SingleAsync(item => item.Id == graph.Process!.Id);

                dbContext.LegalProcesses.Remove(process);
                await dbContext.SaveChangesAsync();
            });

        UploadLegalDocumentUseCase useCase =
            CreateUseCase(scope.ServiceProvider, persistence);

        using var input = new MemoryStream(payload, writable: false);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                null,
                graph.Process!.Id,
                "process-contract.pdf",
                "application/pdf",
                payload.LongLength,
                input));

        Assert.Equal(
            UploadLegalDocumentResultStatus.RelatedProcessUnavailable,
            result.Status);

        LegalDocumentStorageObjectKey objectKey =
            Assert.IsType<LegalDocumentStorageObjectKey>(
                persistence.LastObjectKey);

        await AssertNoDocumentsAsync();
        await AssertObjectMissingAsync(storage, objectKey);

        Assert.Null(persistence.LastLockedState?.Process);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(
            await verificationContext.LegalProcesses
                .AnyAsync(item => item.Id == graph.Process.Id));
    }

    private ServiceProvider CreateServiceProvider()
    {
        DocumentStorageIntegrationEnvironment environment =
            DocumentStorageIntegrationEnvironment.Load();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DocumentStorageOptions.SectionName}:ServiceUrl"] =
                        environment.ServiceUrl,
                    [$"{DocumentStorageOptions.SectionName}:BucketName"] =
                        DocumentStorageIntegrationEnvironment.BucketName,
                    [$"{DocumentStorageOptions.SectionName}:Region"] =
                        DocumentStorageIntegrationEnvironment.Region,
                    [$"{DocumentStorageOptions.SectionName}:ForcePathStyle"] =
                        "true",
                    [$"{DocumentStorageOptions.SectionName}:AccessKey"] =
                        environment.AppAccessKey,
                    [$"{DocumentStorageOptions.SectionName}:SecretKey"] =
                        environment.AppSecretKey,
                    [$"{DocumentStorageOptions.SectionName}:RequireTls"] =
                        "false"
                })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(UploadedAt));
        services.AddInfrastructure(
            fixture.ConnectionString,
            configuration);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static UploadLegalDocumentUseCase CreateUseCase(
        IServiceProvider serviceProvider,
        ILegalDocumentUploadPersistence persistence)
    {
        return new UploadLegalDocumentUseCase(
            serviceProvider.GetRequiredService<OrganizationAccessAuthorization>(),
            serviceProvider.GetRequiredService<IActiveClientInOrganizationLookup>(),
            serviceProvider.GetRequiredService<IProcessOrganizationOwnershipLookup>(),
            serviceProvider.GetRequiredService<ILegalDocumentContentStager>(),
            serviceProvider.GetRequiredService<ILegalDocumentContentInspector>(),
            persistence,
            serviceProvider.GetRequiredService<TimeProvider>());
    }

    private async Task<SeedGraph> SeedGraphAsync(
        OrganizationRole role = OrganizationRole.Owner,
        bool includeClient = false,
        bool includeProcess = false)
    {
        var organization = new Organization(
            "Documents E2E",
            "documents-e2e",
            CreatedAt);
        var user = new User(
            "Documents User",
            "documents@example.com",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);

        Client? client = null;
        LegalProcess? process = null;

        if (includeClient || includeProcess)
        {
            client = new Client(
                organization.Id,
                "Documents Client",
                CreatedAt);
        }

        if (includeProcess)
        {
            process = new LegalProcess(
                organization.Id,
                client!.Id,
                "Documents Process",
                CreatedAt);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        dbContext.Organizations.Add(organization);
        dbContext.Users.Add(user);
        dbContext.OrganizationMemberships.Add(membership);

        if (client is not null)
        {
            dbContext.Clients.Add(client);
        }

        if (process is not null)
        {
            dbContext.LegalProcesses.Add(process);
        }

        await dbContext.SaveChangesAsync();

        return new SeedGraph(
            organization,
            user,
            membership,
            client,
            process);
    }

    private async Task AssertNoDocumentsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        Assert.Equal(
            0,
            await dbContext.LegalDocuments.CountAsync());
    }

    private static async Task AssertObjectMissingAsync(
        ILegalDocumentStorage storage,
        LegalDocumentStorageObjectKey objectKey)
    {
        byte[] probePayload = "compensation-absence-probe"u8.ToArray();
        using var probeInput = new MemoryStream(
            probePayload,
            writable: false);

        // The application principal intentionally has no ListBucket permission,
        // so a missing GET can be indistinguishable from an authorization failure.
        // A conditional no-overwrite PUT on the same opaque key is definitive:
        // it succeeds only when compensation actually removed the prior object.
        await storage.StoreAsync(
            objectKey,
            probeInput,
            probePayload.LongLength,
            CancellationToken.None);

        try
        {
            await using ILegalDocumentStorageReadHandle handle =
                await storage.OpenReadAsync(
                    objectKey,
                    CancellationToken.None);

            using var copy = new MemoryStream();
            await handle.Content.CopyToAsync(copy, CancellationToken.None);
            Assert.Equal(probePayload, copy.ToArray());
        }
        finally
        {
            await storage.DeleteIfExistsAsync(
                objectKey,
                CancellationToken.None);
        }
    }

    private static byte[] CreateValidPdf()
    {
        return "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n9\n%%EOF\n"u8.ToArray();
    }

    private sealed class BeforeExecuteUploadPersistence(
        ILegalDocumentUploadPersistence inner,
        Func<LegalDocumentUploadPersistenceRequest, Task> beforeExecute)
        : ILegalDocumentUploadPersistence
    {
        public LegalDocumentStorageObjectKey? LastObjectKey { get; private set; }

        public LegalDocumentUploadLockedState? LastLockedState { get; private set; }

        public async Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
            LegalDocumentUploadPersistenceRequest request,
            Stream content,
            Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
            CancellationToken cancellationToken = default)
        {
            LastObjectKey = request.ObjectKey;

            await beforeExecute(request);

            return await inner.ExecuteAsync(
                request,
                content,
                lockedState =>
                {
                    LastLockedState = lockedState;
                    return decide(lockedState);
                },
                cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed record SeedGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client,
        LegalProcess? Process);
}
