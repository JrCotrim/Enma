using Enma.Application.Authorization;
using Enma.Application.Processes.Lookup;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Processes.Lookup;

public sealed class SearchLegalProcessesUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "bbaf5576-f3bf-4ab8-9076-cc29478dc96d");

    private static readonly Guid OrganizationId = Guid.Parse(
        "7837c751-4f2c-44b4-8c9f-e292aa9bb293");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithProcessViewRole_AllowsLookup(
        OrganizationRole role)
    {
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(role, queries);

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(SearchLegalProcessesResultStatus.Succeeded, result.Status);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutProcessQuery()
    {
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(SearchLegalProcessesResultStatus.AccessDenied, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithSearchAndPagination_NormalizesAndForwardsContext()
    {
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  Appeal_%\\  ",
            2,
            10,
            cancellationTokenSource.Token);

        Assert.Equal(SearchLegalProcessesResultStatus.Succeeded, result.Status);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal("Appeal_%\\", queries.Search);
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
    public async Task ExecuteAsync_WithBlankSearch_UsesNoLookupFilter(
        string? search)
    {
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(UserId, OrganizationId, search);

        Assert.Null(queries.Search);
    }

    [Fact]
    public async Task ExecuteAsync_WithExtraRow_SetsHasNextAndTrimsPage()
    {
        LegalProcessLookupItem[] legalProcesses = Enumerable.Range(1, 21)
            .Select(index => new LegalProcessLookupItem(
                Guid.NewGuid(),
                $"Process {index:D2}",
                "Client"))
            .ToArray();
        var queries = new FakeLegalProcessLookupQueries(legalProcesses);
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.True(result.HasNext);
        Assert.Equal(20, result.Items.Count);
        Assert.Equal(legalProcesses.Take(20), result.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExtraRow_HasNoNextPage()
    {
        var queries = new FakeLegalProcessLookupQueries(
            [new LegalProcessLookupItem(Guid.NewGuid(), "Only Process", "Client")]);
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
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
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(lookup, queries);

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
    public async Task ExecuteAsync_WithOversizedNormalizedSearch_RejectsBeforeAuthorizationOrQuery()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    $"  {new string('x', 151)}  "));

        Assert.Contains("150", exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaximumNormalizedSearch_AllowsLookup()
    {
        var queries = new FakeLegalProcessLookupQueries();
        SearchLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        string search = new('x', 150);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            $"  {search}  ");

        Assert.Equal(search, queries.Search);
        Assert.Equal(1, queries.CallCount);
    }

    private static SearchLegalProcessesUseCase CreateUseCase(
        OrganizationRole? role,
        FakeLegalProcessLookupQueries queries)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), queries);
    }

    private static SearchLegalProcessesUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeLegalProcessLookupQueries queries)
    {
        return new SearchLegalProcessesUseCase(
            new ProcessActionAuthorization(
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

    private sealed class FakeLegalProcessLookupQueries(
        IReadOnlyList<LegalProcessLookupItem>? legalProcesses = null)
        : ILegalProcessLookupQueries
    {
        public int CallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public string? Search { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<LegalProcessLookupItem>> SearchAsync(
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
                legalProcesses ?? Array.Empty<LegalProcessLookupItem>());
        }
    }
}
