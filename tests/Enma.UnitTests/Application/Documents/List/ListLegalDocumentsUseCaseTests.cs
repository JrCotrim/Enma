using System.Reflection;
using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.List;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Documents.List;

public sealed class ListLegalDocumentsUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "0a74d480-1354-4b83-944d-cff20c6f9b25");
    private static readonly Guid OrganizationId = Guid.Parse(
        "7af4ba00-a975-44e4-8538-aac509c61e47");
    private static readonly Guid MembershipId = Guid.Parse(
        "6676ca9e-92da-49f3-bcef-88d843d6d1a8");
    private static readonly Guid ClientId = Guid.Parse(
        "2fe02cca-d12d-4993-9f4e-3f08a9f1feb4");
    private static readonly Guid ProcessId = Guid.Parse(
        "bb67c620-0357-4d2d-b6a5-3b0f5b0ba478");

    [Fact]
    public async Task ExecuteAsync_WithDeniedAccess_DeniesWithoutDocumentQuery()
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(null, queries);

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(UserId, OrganizationId));

        Assert.Same(ListLegalDocumentsResult.AccessDenied, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDefaults_UsesBoundedPagination()
    {
        var queries = new FakeReadQueries(hasNext: true);
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(UserId, OrganizationId));

        Assert.Equal(ListLegalDocumentsResultStatus.Succeeded, result.Status);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListLegalDocumentsUseCase.DefaultPageSize, result.PageSize);
        Assert.True(result.HasNext);
        Assert.Equal(OrganizationId, queries.Request?.OrganizationId);
        Assert.Null(queries.Request?.FileNameSearch);
        Assert.Null(queries.Request?.ClientId);
        Assert.Null(queries.Request?.ProcessId);
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(1, ListLegalDocumentsUseCase.MaximumPageSize)]
    public async Task ExecuteAsync_WithExplicitPagination_ForwardsExactPage(
        int pageNumber,
        int pageSize)
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(
                UserId,
                OrganizationId,
                PageNumber: pageNumber,
                PageSize: pageSize));

        Assert.Equal(ListLegalDocumentsResultStatus.Succeeded, result.Status);
        Assert.Equal(pageNumber, queries.Request?.PageNumber);
        Assert.Equal(pageSize, queries.Request?.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithFilters_NormalizesSearchAndForwardsIds()
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(new ListLegalDocumentsQuery(
            UserId,
            OrganizationId,
            "  Evidence Final  ",
            ClientId: ClientId));

        Assert.Equal("Evidence Final", queries.Request?.FileNameSearch);
        Assert.Equal(ClientId, queries.Request?.ClientId);
        Assert.Null(queries.Request?.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyPage_ReturnsSuccessfulEmptyResult()
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(UserId, OrganizationId));

        Assert.Equal(ListLegalDocumentsResultStatus.Succeeded, result.Status);
        Assert.Empty(result.Items);
        Assert.False(result.HasNext);
    }

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task ExecuteAsync_WithInvalidInput_ReturnsControlledResult(
        ListLegalDocumentsQuery query)
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(query);

        Assert.Same(ListLegalDocumentsResult.InvalidInput, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCancellationToken()
    {
        var queries = new FakeReadQueries();
        ListLegalDocumentsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(UserId, OrganizationId),
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            queries.CancellationToken);
    }

    [Fact]
    public void MetadataContract_ContainsApprovedFieldsWithoutStorageAuthority()
    {
        string[] propertyNames = typeof(LegalDocumentMetadataReadModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                nameof(LegalDocumentMetadataReadModel.Id),
                nameof(LegalDocumentMetadataReadModel.ClientId),
                nameof(LegalDocumentMetadataReadModel.ProcessId),
                nameof(LegalDocumentMetadataReadModel.OriginalFileName),
                nameof(LegalDocumentMetadataReadModel.ContentType),
                nameof(LegalDocumentMetadataReadModel.SizeBytes),
                nameof(LegalDocumentMetadataReadModel.ContentHashSha256),
                nameof(LegalDocumentMetadataReadModel.UploadedByMembershipId),
                nameof(LegalDocumentMetadataReadModel.CreatedAt)
            ],
            propertyNames);
        Assert.DoesNotContain("StoredObjectKey", propertyNames);
        Assert.DoesNotContain("OrganizationId", propertyNames);
    }

    [Fact]
    public void ListQuery_ContainsNoRequestControlledMembershipOrRole()
    {
        string[] propertyNames = typeof(ListLegalDocumentsQuery)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("MembershipId", propertyNames);
        Assert.DoesNotContain("Role", propertyNames);
        Assert.DoesNotContain("StoredObjectKey", propertyNames);
    }

    public static TheoryData<ListLegalDocumentsQuery> InvalidQueries =>
        new()
        {
            new(UserId, OrganizationId, new string('a', 151)),
            new(UserId, OrganizationId, ProcessId: Guid.Empty),
            new(UserId, OrganizationId, ClientId: Guid.Empty),
            new(
                UserId,
                OrganizationId,
                ProcessId: ProcessId,
                ClientId: ClientId),
            new(UserId, OrganizationId, PageNumber: 0),
            new(UserId, OrganizationId, PageNumber: -1),
            new(UserId, OrganizationId, PageSize: 0),
            new(UserId, OrganizationId, PageSize: 101),
            new(
                UserId,
                OrganizationId,
                PageNumber: int.MaxValue,
                PageSize: 100)
        };

    private static ListLegalDocumentsUseCase CreateUseCase(
        OrganizationRole? role,
        FakeReadQueries queries)
    {
        OrganizationAccessLookupResult? access = role.HasValue
            ? new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                MembershipId,
                role.Value)
            : null;
        var authorization = new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(
                new StubAccessLookup(access)));
        return new ListLegalDocumentsUseCase(authorization, queries);
    }

    private sealed class StubAccessLookup(
        OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
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
            return Task.FromResult(access);
        }
    }

    private sealed class FakeReadQueries(bool hasNext = false)
        : ILegalDocumentReadQueries
    {
        public int ListCallCount { get; private set; }

        public LegalDocumentListReadRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDocumentMetadataReadModel?> FindAsync(
            Guid documentId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<LegalDocumentListReadPage> ListAsync(
            LegalDocumentListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(
                new LegalDocumentListReadPage([], hasNext));
        }
    }
}
