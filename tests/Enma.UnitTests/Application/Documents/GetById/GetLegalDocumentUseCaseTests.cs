using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.GetById;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Documents.GetById;

public sealed class GetLegalDocumentUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "b801d5ac-cb86-4358-8901-c839bd76cd3c");
    private static readonly Guid OrganizationId = Guid.Parse(
        "b7db7c89-f0a8-4906-8307-5a9127073a45");
    private static readonly Guid MembershipId = Guid.Parse(
        "7e1f0632-924b-430f-b64c-2689c73b3d10");
    private static readonly Guid DocumentId = Guid.Parse(
        "42255134-c4f0-482d-966c-2cb115aa9596");

    [Fact]
    public async Task ExecuteAsync_WithDeniedAccess_DeniesWithoutDocumentQuery()
    {
        var queries = new FakeReadQueries();
        GetLegalDocumentUseCase useCase = CreateUseCase(null, queries);

        GetLegalDocumentResult result = await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                UserId,
                OrganizationId,
                DocumentId));

        Assert.Same(GetLegalDocumentResult.AccessDenied, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithSameTenantDocument_ReturnsMetadata()
    {
        LegalDocumentMetadataReadModel expected = CreateMetadata();
        var queries = new FakeReadQueries(expected);
        GetLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        GetLegalDocumentResult result = await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                UserId,
                OrganizationId,
                DocumentId));

        Assert.Equal(GetLegalDocumentResultStatus.Succeeded, result.Status);
        Assert.Same(expected, result.Document);
        Assert.Equal(DocumentId, queries.DocumentId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingDocument_ReturnsNotFound()
    {
        var queries = new FakeReadQueries();
        GetLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        GetLegalDocumentResult result = await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                UserId,
                OrganizationId,
                DocumentId));

        Assert.Same(GetLegalDocumentResult.NotFound, result);
        Assert.Equal(1, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyDocumentId_ReturnsInvalidInput()
    {
        var queries = new FakeReadQueries();
        GetLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        GetLegalDocumentResult result = await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                UserId,
                OrganizationId,
                Guid.Empty));

        Assert.Same(GetLegalDocumentResult.InvalidInput, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCancellationAndContextualKeys()
    {
        var queries = new FakeReadQueries();
        GetLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                UserId,
                OrganizationId,
                DocumentId),
            cancellationTokenSource.Token);

        Assert.Equal(DocumentId, queries.DocumentId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(
            cancellationTokenSource.Token,
            queries.CancellationToken);
    }

    private static GetLegalDocumentUseCase CreateUseCase(
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
        return new GetLegalDocumentUseCase(authorization, queries);
    }

    private static LegalDocumentMetadataReadModel CreateMetadata()
    {
        return new LegalDocumentMetadataReadModel(
            DocumentId,
            null,
            null,
            "contract.pdf",
            "application/pdf",
            10,
            new LegalDocumentContentHash(new byte[32]),
            MembershipId,
            DateTimeOffset.Parse("2026-08-20T12:00:00+00:00"));
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

    private sealed class FakeReadQueries(
        LegalDocumentMetadataReadModel? document = null)
        : ILegalDocumentReadQueries
    {
        public int FindCallCount { get; private set; }

        public Guid DocumentId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDocumentMetadataReadModel?> FindAsync(
            Guid documentId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            DocumentId = documentId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(document);
        }

        public Task<LegalDocumentListReadPage> ListAsync(
            LegalDocumentListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }
    }
}
