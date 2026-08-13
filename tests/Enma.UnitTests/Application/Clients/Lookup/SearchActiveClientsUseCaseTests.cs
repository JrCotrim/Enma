using Enma.Application.Authorization;
using Enma.Application.Clients.Lookup;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.Lookup;

public sealed class SearchActiveClientsUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "ca6c99c4-9663-409f-87e6-af7dac631a66");

    private static readonly Guid OrganizationId = Guid.Parse(
        "ac3e7b68-b431-424a-902c-19bdf59421e8");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithClientViewRole_AllowsLookup(
        OrganizationRole role)
    {
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(role, queries);

        SearchActiveClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(SearchActiveClientsResultStatus.Succeeded, result.Status);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutClientQuery()
    {
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        SearchActiveClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(SearchActiveClientsResultStatus.AccessDenied, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithSearchAndPagination_NormalizesAndForwardsContext()
    {
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        SearchActiveClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  Acme_%\\  ",
            2,
            10,
            cancellationTokenSource.Token);

        Assert.Equal(SearchActiveClientsResultStatus.Succeeded, result.Status);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal("Acme_%\\", queries.Search);
        Assert.Equal(2, queries.PageNumber);
        Assert.Equal(10, queries.PageSize);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(10, result.PageSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithBlankSearch_UsesNoNameFilter(string? search)
    {
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(UserId, OrganizationId, search);

        Assert.Null(queries.Search);
    }

    [Fact]
    public async Task ExecuteAsync_WithExtraRow_SetsHasNextAndTrimsPage()
    {
        ActiveClientLookupItem[] clients = Enumerable.Range(1, 21)
            .Select(index => new ActiveClientLookupItem(
                Guid.NewGuid(),
                $"Client {index:D2}"))
            .ToArray();
        var queries = new FakeActiveClientLookupQueries(clients);
        SearchActiveClientsUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        SearchActiveClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.True(result.HasNext);
        Assert.Equal(20, result.Items.Count);
        Assert.Equal(clients.Take(20), result.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExtraRow_HasNoNextPage()
    {
        var queries = new FakeActiveClientLookupQueries(
            [new ActiveClientLookupItem(Guid.NewGuid(), "Only Client")]);
        SearchActiveClientsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        SearchActiveClientsResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.False(result.HasNext);
        Assert.Single(result.Items);
    }

    [Theory]
    [InlineData(0, 20, "Page number")]
    [InlineData(-1, 20, "Page number")]
    [InlineData(1, 0, "Page size")]
    [InlineData(1, -1, "Page size")]
    [InlineData(1, 101, "Page size")]
    [InlineData(2147483647, 2, "offset")]
    public async Task ExecuteAsync_WithInvalidPagination_RejectsBeforeAuthorizationOrQuery(
        int pageNumber,
        int pageSize,
        string expectedMessage)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    pageNumber: pageNumber,
                    pageSize: pageSize));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithOversizedSearch_RejectsBeforeAuthorizationOrQuery()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeActiveClientLookupQueries();
        SearchActiveClientsUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    new string('x', 151)));

        Assert.Contains("150", exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    private static SearchActiveClientsUseCase CreateUseCase(
        OrganizationRole? role,
        FakeActiveClientLookupQueries queries)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), queries);
    }

    private static SearchActiveClientsUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeActiveClientLookupQueries queries)
    {
        return new SearchActiveClientsUseCase(
            new ClientActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            queries);
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

    private sealed class FakeActiveClientLookupQueries(
        IReadOnlyList<ActiveClientLookupItem>? clients = null)
        : IActiveClientLookupQueries
    {
        public int CallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public string? Search { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<ActiveClientLookupItem>> SearchAsync(
            Guid organizationId,
            string? search,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationId = organizationId;
            Search = search;
            PageNumber = pageNumber;
            PageSize = pageSize;
            CancellationToken = cancellationToken;

            return Task.FromResult(
                clients ?? Array.Empty<ActiveClientLookupItem>());
        }
    }
}
