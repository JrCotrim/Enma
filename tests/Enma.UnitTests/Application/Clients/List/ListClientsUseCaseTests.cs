using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.List;

public sealed class ListClientsUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "ca6c99c4-9663-409f-87e6-af7dac631a66");

    private static readonly Guid OrganizationId = Guid.Parse(
        "ac3e7b68-b431-424a-902c-19bdf59421e8");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutClientQuery()
    {
        var queries = new FakeClientReadQueries();
        ListClientsUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(ListClientsResultStatus.AccessDenied, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberView_QueriesOnlyContextualOrganization()
    {
        ClientReadModel[] clients =
        [
            new(
                Guid.Parse("c0af78b0-d0a5-49ce-8795-6e617de46ba8"),
                "Acme Legal",
                true,
                DateTimeOffset.Parse("2026-08-12T14:00:00+00:00"))
        ];
        var queries = new FakeClientReadQueries(clients);
        ListClientsUseCase useCase = CreateUseCase(OrganizationRole.Member, queries);

        ListClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            2,
            10);

        Assert.Equal(ListClientsResultStatus.Succeeded, result.Status);
        Assert.Equal(clients, result.Items);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(2, queries.PageNumber);
        Assert.Equal(10, queries.PageSize);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExplicitPagination_UsesBoundedDefaults()
    {
        var queries = new FakeClientReadQueries();
        ListClientsUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        ListClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListClientsUseCase.DefaultPageSize, result.PageSize);
        Assert.Equal(1, queries.PageNumber);
        Assert.Equal(ListClientsUseCase.DefaultPageSize, queries.PageSize);
    }

    [Theory]
    [InlineData(0, 20, "Page number")]
    [InlineData(-1, 20, "Page number")]
    [InlineData(1, 0, "Page size")]
    [InlineData(1, -1, "Page size")]
    [InlineData(1, 101, "Page size")]
    public async Task ExecuteAsync_WithInvalidPagination_RejectsBeforeAuthorizationOrQuery(
        int pageNumber,
        int pageSize,
        string expectedMessage)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeClientReadQueries();
        ListClientsUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    pageNumber,
                    pageSize));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_ForwardsTokenToQuery()
    {
        var queries = new FakeClientReadQueries();
        ListClientsUseCase useCase = CreateUseCase(OrganizationRole.Owner, queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            1,
            20,
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static ListClientsUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientReadQueries queries)
    {
        return CreateUseCase(
            new StubOrganizationAccessLookup(role),
            queries);
    }

    private static ListClientsUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeClientReadQueries queries)
    {
        var actionAuthorization = new ClientActionAuthorization(
            new OrganizationAccessAuthorization(lookup));

        return new ListClientsUseCase(actionAuthorization, queries);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(role);
        }
    }

    private sealed class FakeClientReadQueries(
        IReadOnlyList<ClientReadModel>? clients = null) : IClientReadQueries
    {
        public int ListCallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientReadModel?> FindAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "FindAsync must not be called by List Clients tests.");
        }

        public Task<IReadOnlyList<ClientReadModel>> ListAsync(
            Guid organizationId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            OrganizationId = organizationId;
            PageNumber = pageNumber;
            PageSize = pageSize;
            CancellationToken = cancellationToken;

            return Task.FromResult(
                clients ?? Array.Empty<ClientReadModel>());
        }
    }
}
