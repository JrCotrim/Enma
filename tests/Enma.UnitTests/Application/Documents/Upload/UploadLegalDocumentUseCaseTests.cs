using System.Reflection;
using Enma.Application.Authorization;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Upload;
using Enma.Application.Processes;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Documents.Upload;

public sealed class UploadLegalDocumentUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "0a6c00a2-d378-4b1d-aa4a-262611cf8965");

    private static readonly Guid OrganizationId = Guid.Parse(
        "9981c3bb-b5c0-47b1-ae56-c872c99b9e70");

    private static readonly Guid ActorMembershipId = Guid.Parse(
        "dbb5a235-d752-4668-ae8b-52c0b9edbb75");

    private static readonly Guid ClientId = Guid.Parse(
        "7bd3a9cb-fde2-49ef-845d-372cd22a1dbb");

    private static readonly Guid ProcessId = Guid.Parse(
        "67609162-7546-425f-9ae5-c1d758335a54");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
        16,
        0,
        0,
        TimeSpan.Zero);

    private static readonly byte[] ContentHash =
        Enumerable.Range(0, 32)
            .Select(value => (byte)value)
            .ToArray();

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_AllActiveRoles_CanUploadGeneralDocument(
        OrganizationRole role)
    {
        TestDependencies dependencies = CreateDependencies(role);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(UploadLegalDocumentResultStatus.Succeeded, result.Status);
        Assert.NotNull(result.DocumentId);
        Assert.Equal(1, dependencies.Persistence.CallCount);
        Assert.Equal(
            ActorMembershipId,
            dependencies.Persistence.PersistedDocument?.UploadedByMembershipId);
        Assert.Equal(
            OrganizationId,
            dependencies.Persistence.PersistedDocument?.OrganizationId);
        Assert.Null(dependencies.Persistence.PersistedDocument?.ClientId);
        Assert.Null(dependencies.Persistence.PersistedDocument?.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedOrganizationAccess_StopsBeforeFileWork()
    {
        TestDependencies dependencies = CreateDependencies(
            (OrganizationRole?)null);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Same(UploadLegalDocumentResult.AccessDenied, result);
        Assert.Equal(0, dependencies.Stager.CallCount);
        Assert.Equal(0, dependencies.Inspector.CallCount);
        Assert.Equal(0, dependencies.ClientLookup.CallCount);
        Assert.Equal(0, dependencies.ProcessLookup.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AccessWithMissingMembership_DeniesBeforeFileWork()
    {
        var access = new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            null,
            OrganizationRole.Owner);
        TestDependencies dependencies = CreateDependencies(access);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Same(UploadLegalDocumentResult.AccessDenied, result);
        Assert.Equal(0, dependencies.Stager.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_EmptyOptionalClassificationId_ReturnsInvalidInput(
        bool emptyClient,
        bool emptyProcess)
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(
                clientId: emptyClient ? Guid.Empty : null,
                processId: emptyProcess ? Guid.Empty : null));

        Assert.Same(UploadLegalDocumentResult.InvalidInput, result);
        Assert.Equal(0, dependencies.Stager.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ClientAndProcessTogether_ReturnsInvalidInput()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(
                clientId: ClientId,
                processId: ProcessId));

        Assert.Same(UploadLegalDocumentResult.InvalidInput, result);
        Assert.Equal(0, dependencies.Stager.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AdmissionRejection_ReturnsSafeReasonBeforeStaging()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Member);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(
                originalFileName: "malware.exe",
                submittedContentType: "application/octet-stream"));

        Assert.Equal(UploadLegalDocumentResultStatus.Rejected, result.Status);
        Assert.Equal(
            LegalDocumentUploadRejectionReason.UnsupportedFileType,
            result.RejectionReason);
        Assert.Equal(0, dependencies.Stager.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_StagingRejection_ReturnsSafeReasonWithoutInspectionOrPersistence()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Stager.RejectionReason =
            LegalDocumentUploadRejectionReason.ContentLengthMismatch;
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(UploadLegalDocumentResultStatus.Rejected, result.Status);
        Assert.Equal(
            LegalDocumentUploadRejectionReason.ContentLengthMismatch,
            result.RejectionReason);
        Assert.Equal(0, dependencies.Inspector.CallCount);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_InspectionRejection_DisposesStagedContentAndDoesNotPersist()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Administrator);
        dependencies.Inspector.RejectionReason =
            LegalDocumentUploadRejectionReason.InvalidFileContent;
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(UploadLegalDocumentResultStatus.Rejected, result.Status);
        Assert.Equal(
            LegalDocumentUploadRejectionReason.InvalidFileContent,
            result.RejectionReason);
        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableDirectClient_ReturnsGenericResultAfterInspection()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Member);
        dependencies.ClientLookup.Exists = false;
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(clientId: ClientId));

        Assert.Same(
            UploadLegalDocumentResult.RelatedClientUnavailable,
            result);
        Assert.Equal(1, dependencies.Inspector.CallCount);
        Assert.Equal(ClientId, dependencies.ClientLookup.ClientId);
        Assert.Equal(OrganizationId, dependencies.ClientLookup.OrganizationId);
        Assert.Equal(0, dependencies.Persistence.CallCount);
        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableProcess_ReturnsGenericResultAfterInspection()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.ProcessLookup.Exists = false;
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(processId: ProcessId));

        Assert.Same(
            UploadLegalDocumentResult.RelatedProcessUnavailable,
            result);
        Assert.Equal(1, dependencies.Inspector.CallCount);
        Assert.Equal(ProcessId, dependencies.ProcessLookup.ProcessId);
        Assert.Equal(OrganizationId, dependencies.ProcessLookup.OrganizationId);
        Assert.Equal(0, dependencies.Persistence.CallCount);
        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_ValidClientUpload_PassesCanonicalPreparedMetadataToPersistence()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Member);
        dependencies.Persistence.LockedState = CreateLockedState(
            OrganizationRole.Member,
            clientId: ClientId);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();
        using var source = new MemoryStream([1, 2, 3, 4]);

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(
                content: source,
                clientId: ClientId,
                originalFileName: "re\u0301sum\u00e9.pdf",
                submittedContentType: "APPLICATION/PDF; charset=binary"));

        Assert.Equal(UploadLegalDocumentResultStatus.Succeeded, result.Status);
        Assert.NotNull(dependencies.Persistence.Request);
        Assert.Equal(UserId, dependencies.Persistence.Request.UserId);
        Assert.Equal(OrganizationId, dependencies.Persistence.Request.OrganizationId);
        Assert.Equal(
            ActorMembershipId,
            dependencies.Persistence.Request.ActorMembershipId);
        Assert.Equal(ClientId, dependencies.Persistence.Request.ClientId);
        Assert.Null(dependencies.Persistence.Request.ProcessId);
        Assert.Equal(
            "r\u00e9sum\u00e9.pdf",
            dependencies.Persistence.Request.OriginalFileName);
        Assert.Equal(
            "application/pdf",
            dependencies.Persistence.Request.CanonicalContentType);
        Assert.Equal(4, dependencies.Persistence.Request.ContentLength);
        Assert.Equal(
            ContentHash,
            dependencies.Persistence.Request.ContentHashSha256.ToArray());
        Assert.Equal(
            32,
            dependencies.Persistence.Request.ObjectKey.Value.Length);
        Assert.True(
            dependencies.Persistence.Request.ObjectKey.Value.All(
                character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.Same(
            dependencies.Stager.LastStagedContent?.Content,
            dependencies.Persistence.Content);
        Assert.True(source.CanRead);
        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessUpload_DoesNotPopulateDirectClientClassification()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Administrator);
        dependencies.Persistence.LockedState = CreateLockedState(
            OrganizationRole.Administrator,
            processId: ProcessId);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(processId: ProcessId));

        Assert.Equal(UploadLegalDocumentResultStatus.Succeeded, result.Status);
        Assert.Null(dependencies.Persistence.PersistedDocument?.ClientId);
        Assert.Equal(
            ProcessId,
            dependencies.Persistence.PersistedDocument?.ProcessId);
        Assert.Equal(0, dependencies.ClientLookup.CallCount);
        Assert.Equal(1, dependencies.ProcessLookup.CallCount);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ExecuteAsync_FinalActorRevalidationFails_DeniesWithoutCreatingDocument(
        bool membershipActive,
        bool userActive,
        bool organizationActive)
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Persistence.LockedState = new LegalDocumentUploadLockedState(
            new LegalDocumentUploadActorState(
                UserId,
                OrganizationId,
                ActorMembershipId,
                OrganizationRole.Owner,
                membershipActive,
                userActive,
                organizationActive),
            null,
            null);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Same(UploadLegalDocumentResult.AccessDenied, result);
        Assert.Null(dependencies.Persistence.PersistedDocument);
        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_FinalActorRoleIsUnknown_Denies()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Persistence.LockedState = new LegalDocumentUploadLockedState(
            new LegalDocumentUploadActorState(
                UserId,
                OrganizationId,
                ActorMembershipId,
                (OrganizationRole)999,
                true,
                true,
                true),
            null,
            null);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Same(UploadLegalDocumentResult.AccessDenied, result);
        Assert.Null(dependencies.Persistence.PersistedDocument);
    }

    [Fact]
    public async Task ExecuteAsync_FinalClientRevalidationFails_ReturnsUnavailable()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Member);
        dependencies.Persistence.LockedState = new LegalDocumentUploadLockedState(
            CreateActor(OrganizationRole.Member),
            new LegalDocumentUploadClientState(
                ClientId,
                OrganizationId,
                false),
            null);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(clientId: ClientId));

        Assert.Same(
            UploadLegalDocumentResult.RelatedClientUnavailable,
            result);
        Assert.Null(dependencies.Persistence.PersistedDocument);
    }

    [Fact]
    public async Task ExecuteAsync_FinalProcessTenantMismatch_ReturnsUnavailable()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Persistence.LockedState = new LegalDocumentUploadLockedState(
            CreateActor(OrganizationRole.Owner),
            null,
            new LegalDocumentUploadProcessState(
                ProcessId,
                Guid.NewGuid()));
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand(processId: ProcessId));

        Assert.Same(
            UploadLegalDocumentResult.RelatedProcessUnavailable,
            result);
        Assert.Null(dependencies.Persistence.PersistedDocument);
    }

    [Fact]
    public async Task ExecuteAsync_Success_CreatesImmutableMetadataWithServerTimeAndActorMembership()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Persistence.LockedState = CreateLockedState(
            OrganizationRole.Owner);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        LegalDocument? document = dependencies.Persistence.PersistedDocument;

        Assert.Equal(UploadLegalDocumentResultStatus.Succeeded, result.Status);
        Assert.NotNull(document);
        Assert.Equal(result.DocumentId, document.Id);
        Assert.Equal(OrganizationId, document.OrganizationId);
        Assert.Equal(ActorMembershipId, document.UploadedByMembershipId);
        Assert.NotEqual(UserId, document.UploadedByMembershipId);
        Assert.Equal(CreatedAt, document.CreatedAt);
        Assert.Equal("evidence.pdf", document.OriginalFileName);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(4, document.SizeBytes);
        Assert.Equal(ContentHash, document.ContentHashSha256.ToArray());
        Assert.Equal(
            dependencies.Persistence.Request?.ObjectKey.Value,
            document.StoredObjectKey);
    }

    [Fact]
    public async Task ExecuteAsync_PersistenceFailure_StillDisposesStagedContent()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Owner);
        dependencies.Persistence.ExceptionToThrow =
            new InvalidOperationException("simulated persistence failure");
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(CreateCommand()));

        Assert.True(dependencies.Stager.LastStagedContent?.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCallerCancellationToEveryReachedBoundary()
    {
        TestDependencies dependencies = CreateDependencies(
            OrganizationRole.Administrator);
        dependencies.Persistence.LockedState = CreateLockedState(
            OrganizationRole.Administrator,
            processId: ProcessId);
        UploadLegalDocumentUseCase useCase = dependencies.CreateUseCase();
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            CreateCommand(processId: ProcessId),
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.AccessLookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.Stager.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.Inspector.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.ProcessLookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.Persistence.CancellationToken);
    }

    [Fact]
    public void UploadCommand_ContainsNoClientControlledAuthorityOrStorageFields()
    {
        string[] propertyNames = typeof(UploadLegalDocumentCommand)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                nameof(UploadLegalDocumentCommand.ClientId),
                nameof(UploadLegalDocumentCommand.Content),
                nameof(UploadLegalDocumentCommand.DeclaredContentLength),
                nameof(UploadLegalDocumentCommand.OrganizationId),
                nameof(UploadLegalDocumentCommand.OriginalFileName),
                nameof(UploadLegalDocumentCommand.ProcessId),
                nameof(UploadLegalDocumentCommand.SubmittedContentType),
                nameof(UploadLegalDocumentCommand.UserId)
            ],
            propertyNames);
        Assert.DoesNotContain("ActorMembershipId", propertyNames);
        Assert.DoesNotContain("UploadedByMembershipId", propertyNames);
        Assert.DoesNotContain("Role", propertyNames);
        Assert.DoesNotContain("StoredObjectKey", propertyNames);
        Assert.DoesNotContain("CanonicalContentType", propertyNames);
        Assert.DoesNotContain("ContentHashSha256", propertyNames);
        Assert.DoesNotContain("CreatedAt", propertyNames);
        Assert.DoesNotContain("TenantId", propertyNames);
    }

    private static TestDependencies CreateDependencies(
        OrganizationRole? role = OrganizationRole.Owner)
    {
        OrganizationAccessLookupResult? access = role is null
            ? null
            : new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                ActorMembershipId,
                role.Value);

        return new TestDependencies(access);
    }

    private static TestDependencies CreateDependencies(
        OrganizationAccessLookupResult access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return new TestDependencies(access);
    }

    private static UploadLegalDocumentCommand CreateCommand(
        Stream? content = null,
        Guid? clientId = null,
        Guid? processId = null,
        string? originalFileName = "evidence.pdf",
        string? submittedContentType = "application/pdf",
        long declaredContentLength = 4)
    {
        return new UploadLegalDocumentCommand(
            UserId,
            OrganizationId,
            clientId,
            processId,
            originalFileName,
            submittedContentType,
            declaredContentLength,
            content ?? new MemoryStream([1, 2, 3, 4]));
    }

    private static LegalDocumentUploadLockedState CreateLockedState(
        OrganizationRole role,
        Guid? clientId = null,
        Guid? processId = null)
    {
        return new LegalDocumentUploadLockedState(
            CreateActor(role),
            clientId is Guid relatedClientId
                ? new LegalDocumentUploadClientState(
                    relatedClientId,
                    OrganizationId,
                    true)
                : null,
            processId is Guid relatedProcessId
                ? new LegalDocumentUploadProcessState(
                    relatedProcessId,
                    OrganizationId)
                : null);
    }

    private static LegalDocumentUploadActorState CreateActor(
        OrganizationRole role)
    {
        return new LegalDocumentUploadActorState(
            UserId,
            OrganizationId,
            ActorMembershipId,
            role,
            true,
            true,
            true);
    }

    private sealed class TestDependencies
    {
        public TestDependencies(OrganizationAccessLookupResult? access)
        {
            AccessLookup = new StubOrganizationAccessLookup(access);
            ClientLookup = new StubActiveClientLookup();
            ProcessLookup = new StubProcessOwnershipLookup();
            Stager = new StubContentStager();
            Inspector = new StubContentInspector();
            Persistence = new StubUploadPersistence
            {
                LockedState = CreateLockedState(
                    access?.Role ?? OrganizationRole.Owner)
            };
        }

        public StubOrganizationAccessLookup AccessLookup { get; }

        public StubActiveClientLookup ClientLookup { get; }

        public StubProcessOwnershipLookup ProcessLookup { get; }

        public StubContentStager Stager { get; }

        public StubContentInspector Inspector { get; }

        public StubUploadPersistence Persistence { get; }

        public UploadLegalDocumentUseCase CreateUseCase()
        {
            return new UploadLegalDocumentUseCase(
                new OrganizationAccessAuthorization(AccessLookup),
                ClientLookup,
                ProcessLookup,
                Stager,
                Inspector,
                Persistence,
                new FixedTimeProvider(CreatedAt));
        }
    }

    private sealed class StubOrganizationAccessLookup(
        OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            return Task.FromResult(
                access is null
                    ? null
                    : (OrganizationRole?)access.Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            return Task.FromResult(access);
        }
    }

    private sealed class StubActiveClientLookup : IActiveClientInOrganizationLookup
    {
        public bool Exists { get; set; } = true;

        public int CallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> ExistsAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ClientId = clientId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(Exists);
        }
    }

    private sealed class StubProcessOwnershipLookup
        : IProcessOrganizationOwnershipLookup
    {
        public bool Exists { get; set; } = true;

        public int CallCount { get; private set; }

        public Guid ProcessId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> ExistsInOrganizationAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ProcessId = processId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(Exists);
        }
    }

    private sealed class StubContentStager : ILegalDocumentContentStager
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public LegalDocumentUploadRejectionReason? RejectionReason { get; set; }

        public StubStagedContent? LastStagedContent { get; private set; }

        public Task<ILegalDocumentStagedContent> StageAsync(
            Stream source,
            long declaredContentLength,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;

            if (RejectionReason is LegalDocumentUploadRejectionReason reason)
            {
                throw new LegalDocumentUploadRejectedException(reason);
            }

            LastStagedContent = new StubStagedContent(
                declaredContentLength,
                ContentHash);

            return Task.FromResult<ILegalDocumentStagedContent>(
                LastStagedContent);
        }
    }

    private sealed class StubStagedContent(
        long contentLength,
        byte[] contentHash) : ILegalDocumentStagedContent
    {
        private readonly MemoryStream _content =
            new(new byte[checked((int)contentLength)]);

        public Stream Content => _content;

        public long ContentLength { get; } = contentLength;

        public ReadOnlyMemory<byte> ContentHashSha256 { get; } =
            new((byte[])contentHash.Clone());

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _content.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubContentInspector : ILegalDocumentContentInspector
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public LegalDocumentUploadRejectionReason? RejectionReason { get; set; }

        public Task InspectAsync(
            Stream content,
            long contentLength,
            LegalDocumentFileType fileType,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;

            if (RejectionReason is LegalDocumentUploadRejectionReason reason)
            {
                throw new LegalDocumentUploadRejectedException(reason);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubUploadPersistence : ILegalDocumentUploadPersistence
    {
        public int CallCount { get; private set; }

        public LegalDocumentUploadPersistenceRequest? Request { get; private set; }

        public Stream? Content { get; private set; }

        public LegalDocument? PersistedDocument { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public LegalDocumentUploadLockedState LockedState { get; set; } =
            CreateLockedState(OrganizationRole.Owner);

        public Exception? ExceptionToThrow { get; set; }

        public Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
            LegalDocumentUploadPersistenceRequest request,
            Stream content,
            Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            Content = content;
            CancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            LegalDocumentUploadDecision decision = decide(LockedState);

            return decision.Status switch
            {
                LegalDocumentUploadDecisionStatus.AccessDenied =>
                    Task.FromResult(
                        LegalDocumentUploadPersistenceResult.AccessDenied),
                LegalDocumentUploadDecisionStatus.RelatedClientUnavailable =>
                    Task.FromResult(
                        LegalDocumentUploadPersistenceResult.RelatedClientUnavailable),
                LegalDocumentUploadDecisionStatus.RelatedProcessUnavailable =>
                    Task.FromResult(
                        LegalDocumentUploadPersistenceResult.RelatedProcessUnavailable),
                LegalDocumentUploadDecisionStatus.Persist
                    when decision.LegalDocument is LegalDocument legalDocument =>
                    PersistAsync(legalDocument),
                _ => throw new InvalidOperationException(
                    "The test persistence received an invalid upload decision.")
            };
        }

        private Task<LegalDocumentUploadPersistenceResult> PersistAsync(
            LegalDocument legalDocument)
        {
            PersistedDocument = legalDocument;

            return Task.FromResult(
                LegalDocumentUploadPersistenceResult.Persisted(
                    legalDocument.Id));
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
