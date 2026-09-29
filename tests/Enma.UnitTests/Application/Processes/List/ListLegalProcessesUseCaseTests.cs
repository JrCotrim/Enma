using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Application.Processes.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;

namespace Enma.UnitTests.Application.Processes.List;

public sealed class ListLegalProcessesUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "5d504351-0ec8-4394-9317-808445fc52b4");

    private static readonly Guid OrganizationId = Guid.Parse(
        "be443985-22db-4e4a-8b49-1b61eb2b5312");

    private static readonly Guid ActorMembershipId = Guid.Parse(
        "0c1f2fd4-5a41-4b7e-9fa2-6d8b6f6b1c55");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutProcessQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId));

        Assert.Equal(ListLegalProcessesResultStatus.AccessDenied, result.Status);
        Assert.Empty(result.Items);
        Assert.False(result.HasNext);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithViewRole_QueriesOnlyContextualOrganization(
        OrganizationRole role)
    {
        LegalProcessReadModel[] legalProcesses =
        [
            new(
                Guid.Parse("77a319ea-840c-47bb-935f-944191520287"),
                "Contract Review",
                Guid.Parse("381c55b5-1d4d-4143-8071-521ac89cde66"),
                "Acme Legal",
                DateTimeOffset.Parse("2026-08-13T14:00:00+00:00"),
                null,
                LegalProcessStatus.InProgress,
                null,
                null,
                null)
        ];
        var queries = new FakeLegalProcessReadQueries(legalProcesses);
        ListLegalProcessesUseCase useCase = CreateUseCase(role, queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                PageNumber: 2,
                PageSize: 10));

        Assert.Equal(ListLegalProcessesResultStatus.Succeeded, result.Status);
        Assert.Equal(legalProcesses, result.Items);
        Assert.False(result.HasNext);
        LegalProcessListReadRequest request = Assert.IsType<LegalProcessListReadRequest>(
            queries.Request);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(2, request.PageNumber);
        Assert.Equal(10, request.PageSize);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutParameters_UsesBoundedDefaultsWithoutFilters()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId));

        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListLegalProcessesUseCase.DefaultPageSize, result.PageSize);
        Assert.Equal(
            new LegalProcessListReadRequest(
                OrganizationId,
                null,
                null,
                LegalProcessReadResponsibleFilterKind.Any,
                null,
                LegalProcessListSort.Title,
                1,
                ListLegalProcessesUseCase.DefaultPageSize),
            queries.Request);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaximumPageSize_ForwardsBoundedMaximum()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                PageSize: ListLegalProcessesUseCase.MaximumPageSize));

        Assert.Equal(
            ListLegalProcessesUseCase.MaximumPageSize,
            result.PageSize);
        Assert.Equal(
            ListLegalProcessesUseCase.MaximumPageSize,
            queries.Request?.PageSize);
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
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    new ListLegalProcessesQuery(
                        UserId,
                        OrganizationId,
                        PageNumber: pageNumber,
                        PageSize: pageSize)));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Theory]
    [InlineData("inProgress", LegalProcessStatus.InProgress)]
    [InlineData("suspended", LegalProcessStatus.Suspended)]
    [InlineData("closed", LegalProcessStatus.Closed)]
    public async Task ExecuteAsync_WithStatus_ForwardsParsedStatus(
        string status,
        LegalProcessStatus expected)
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId, Status: status));

        Assert.Equal(expected, queries.Request?.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WithSearchAndNewestSort_TrimsAndForwards()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                Search: "  0001234-56_%\\  ",
                Sort: "newest"));

        Assert.Equal("0001234-56_%\\", queries.Request?.Search);
        Assert.Equal(LegalProcessListSort.Newest, queries.Request?.Sort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithBlankSearch_DoesNotFilter(string search)
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId, Search: search));

        Assert.Null(queries.Request?.Search);
    }

    [Fact]
    public async Task ExecuteAsync_WithSearchAtMaximumAfterTrim_Forwards()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);
        string search = new('x', ListLegalProcessesUseCase.MaximumSearchLength);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                Search: $"  {search}  "));

        Assert.Equal(search, queries.Request?.Search);
    }

    [Theory]
    [InlineData(null, LegalProcessReadResponsibleFilterKind.Any)]
    [InlineData("any", LegalProcessReadResponsibleFilterKind.Any)]
    [InlineData("ANY", LegalProcessReadResponsibleFilterKind.Any)]
    [InlineData("unassigned", LegalProcessReadResponsibleFilterKind.Unassigned)]
    public async Task ExecuteAsync_WithKeywordResponsible_ForwardsKindWithoutMembership(
        string? responsible,
        LegalProcessReadResponsibleFilterKind expected)
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                Responsible: responsible));

        Assert.Equal(expected, queries.Request?.ResponsibleFilterKind);
        Assert.Null(queries.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("Self")]
    public async Task ExecuteAsync_WithSelfResponsible_UsesAuthorizedActorMembership(
        string responsible)
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                Responsible: responsible));

        Assert.Equal(
            LegalProcessReadResponsibleFilterKind.Membership,
            queries.Request?.ResponsibleFilterKind);
        Assert.Equal(ActorMembershipId, queries.Request?.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMembershipResponsible_ForwardsRequestedMembership()
    {
        Guid membershipId = Guid.Parse("8f9a8a2e-1c34-4b39-9a55-0e7c11d6f8a1");
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(
                UserId,
                OrganizationId,
                Responsible: membershipId.ToString("D")));

        Assert.Equal(
            LegalProcessReadResponsibleFilterKind.Membership,
            queries.Request?.ResponsibleFilterKind);
        Assert.Equal(membershipId, queries.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [MemberData(nameof(InvalidFilterQueries))]
    public async Task ExecuteAsync_WithInvalidFilter_RejectsBeforeAuthorizationOrQuery(
        string? search,
        string? status,
        string? responsible,
        string? sort)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(lookup, queries);

        await Assert.ThrowsAsync<RequestValidationException>(
            () => useCase.ExecuteAsync(
                new ListLegalProcessesQuery(
                    UserId,
                    OrganizationId,
                    search,
                    status,
                    responsible,
                    sort)));

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithExtraRow_SetsHasNextAndTrimsPage()
    {
        LegalProcessReadModel[] legalProcesses = Enumerable.Range(1, 3)
            .Select(index => new LegalProcessReadModel(
                Guid.NewGuid(),
                $"Process {index}",
                Guid.NewGuid(),
                "Client",
                DateTimeOffset.Parse("2026-08-13T14:00:00+00:00"),
                null,
                LegalProcessStatus.InProgress,
                null,
                null,
                null))
            .ToArray();
        var queries = new FakeLegalProcessReadQueries(legalProcesses);
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalProcessesResult withExtraRow = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId, PageSize: 2));
        ListLegalProcessesResult exactPage = await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId, PageSize: 3));

        Assert.True(withExtraRow.HasNext);
        Assert.Equal(legalProcesses.Take(2), withExtraRow.Items);
        Assert.False(exactPage.HasNext);
        Assert.Equal(legalProcesses, exactPage.Items);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_ForwardsTokenToQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            new ListLegalProcessesQuery(UserId, OrganizationId),
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    public static TheoryData<string?, string?, string?, string?> InvalidFilterQueries =>
        new()
        {
            { new string('x', 151), null, null, null },
            { null, "InProgress", null, null },
            { null, "archived", null, null },
            { null, string.Empty, null, null },
            { null, null, Guid.Empty.ToString("D"), null },
            { null, null, "not-a-guid", null },
            { null, null, string.Empty, null },
            { null, null, Guid.NewGuid().ToString("N"), null },
            { null, null, null, "Newest" },
            { null, null, null, "oldest" },
            { null, null, null, string.Empty }
        };

    private static ListLegalProcessesUseCase CreateUseCase(
        OrganizationRole? role,
        FakeLegalProcessReadQueries queries)
    {
        return CreateUseCase(
            new StubOrganizationAccessLookup(role),
            queries);
    }

    private static ListLegalProcessesUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeLegalProcessReadQueries queries)
    {
        var actionAuthorization = new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(lookup));

        return new ListLegalProcessesUseCase(actionAuthorization, queries);
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

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationAccessLookupResult? access = role is OrganizationRole value
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    ActorMembershipId,
                    value)
                : null;
            return Task.FromResult(access);
        }
    }

    private sealed class FakeLegalProcessReadQueries(
        IReadOnlyList<LegalProcessReadModel>? legalProcesses = null)
        : ILegalProcessReadQueries
    {
        public int ListCallCount { get; private set; }

        public LegalProcessListReadRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalProcessReadModel?> FindAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "FindAsync must not be called by List Legal Processes tests.");
        }

        public Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
            LegalProcessListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            return Task.FromResult(
                legalProcesses ?? Array.Empty<LegalProcessReadModel>());
        }
    }
}
