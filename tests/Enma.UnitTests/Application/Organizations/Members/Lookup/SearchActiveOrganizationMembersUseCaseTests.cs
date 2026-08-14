using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Lookup;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Members.Lookup;

public sealed class SearchActiveOrganizationMembersUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "4126a347-5f5c-4a8e-8522-5ee169351a77");

    private static readonly Guid OrganizationId = Guid.Parse(
        "f013a750-033e-46db-b920-8d290ca07a75");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithActiveOrganizationRole_AllowsLookup(
        OrganizationRole role)
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            role,
            queries);

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(
            SearchActiveOrganizationMembersResultStatus.Succeeded,
            result.Status);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedOrganizationAccess_ShortCircuitsMemberQuery()
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(
            SearchActiveOrganizationMembersResultStatus.AccessDenied,
            result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithSearchAndPagination_NormalizesAndForwardsContext()
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  Member_%\\  ",
            2,
            10,
            cancellationTokenSource.Token);

        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal("Member_%\\", queries.Search);
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
    public async Task ExecuteAsync_WithBlankSearch_UsesNoDisplayNameFilter(
        string? search)
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(UserId, OrganizationId, search);

        Assert.Null(queries.Search);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaximumNormalizedSearch_AcceptsSearch()
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);
        string search = $"  {new string('x', 150)}  ";

        await useCase.ExecuteAsync(UserId, OrganizationId, search);

        Assert.Equal(new string('x', 150), queries.Search);
    }

    [Fact]
    public async Task ExecuteAsync_WithDefaults_UsesFirstPageOfTwenty()
    {
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(1, queries.PageNumber);
        Assert.Equal(20, queries.PageSize);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(20, result.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithExtraRow_SetsHasNextAndReturnsBoundedPage()
    {
        OrganizationMemberLookupItem[] members = Enumerable.Range(1, 21)
            .Select(index => new OrganizationMemberLookupItem(
                Guid.NewGuid(),
                $"Member {index:D2}"))
            .ToArray();
        var queries = new FakeOrganizationMemberLookupQueries(members);
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.True(result.HasNext);
        Assert.Equal(20, result.Items.Count);
        Assert.Equal(members.Take(20), result.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExtraRow_HasNoNextPage()
    {
        var queries = new FakeOrganizationMemberLookupQueries(
            [new OrganizationMemberLookupItem(Guid.NewGuid(), "Only Member")]);
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
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
    public async Task ExecuteAsync_WithInvalidPagination_RejectsBeforeAccessOrQuery(
        int pageNumber,
        int pageSize,
        string expectedMessage)
    {
        var accessLookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            accessLookup,
            queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    pageNumber: pageNumber,
                    pageSize: pageSize));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithOversizedNormalizedSearch_RejectsBeforeAccessOrQuery()
    {
        var accessLookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeOrganizationMemberLookupQueries();
        SearchActiveOrganizationMembersUseCase useCase = CreateUseCase(
            accessLookup,
            queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    new string('x', 151)));

        Assert.Contains("150", exception.Message);
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    private static SearchActiveOrganizationMembersUseCase CreateUseCase(
        OrganizationRole? role,
        FakeOrganizationMemberLookupQueries queries)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), queries);
    }

    private static SearchActiveOrganizationMembersUseCase CreateUseCase(
        IOrganizationAccessLookup accessLookup,
        FakeOrganizationMemberLookupQueries queries)
    {
        return new SearchActiveOrganizationMembersUseCase(
            new OrganizationAccessAuthorization(accessLookup),
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

    private sealed class FakeOrganizationMemberLookupQueries(
        IReadOnlyList<OrganizationMemberLookupItem>? members = null)
        : IOrganizationMemberLookupQueries
    {
        public int CallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public string? Search { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<OrganizationMemberLookupItem>> SearchAsync(
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
                members ?? Array.Empty<OrganizationMemberLookupItem>());
        }
    }
}
