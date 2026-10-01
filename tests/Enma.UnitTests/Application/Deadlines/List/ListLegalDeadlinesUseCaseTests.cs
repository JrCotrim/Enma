using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.List;

public sealed class ListLegalDeadlinesUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "055612a9-646a-47df-ab02-5e60ca65d27a");

    private static readonly Guid OrganizationId = Guid.Parse(
        "34902977-7b86-4b65-937a-167dc40c7635");

    private static readonly Guid ActorMembershipId = Guid.Parse(
        "4d1f6a3e-2b7c-4c1a-9e55-71a0f2b6c8d9");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutDeadlineQuery()
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Same(ListLegalDeadlinesResult.AccessDenied, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithViewRole_QueriesOnlyContextualOrganization(
        OrganizationRole role)
    {
        LegalDeadlineListItem[] deadlines =
        [
            new(
                Guid.Parse("36139917-5592-4014-906b-47a4c6a79351"),
                "Pending Deadline",
                new DateOnly(2026, 9, 15),
                Guid.Parse("cf35d5bc-6ea4-413a-b5bd-654aec435bc4"),
                "Matter",
                "Client",
                LegalDeadlineReadState.Pending,
                Guid.Parse("a3c1f0d2-5e6b-4f7a-8c9d-0e1f2a3b4c5d"),
                "Responsible Member"),
            new(
                Guid.Parse("db479f21-33c9-48c2-aa0c-4d6aa89a83b5"),
                "Completed Deadline",
                new DateOnly(2026, 9, 16),
                Guid.Parse("cf35d5bc-6ea4-413a-b5bd-654aec435bc4"),
                "Matter",
                "Client",
                LegalDeadlineReadState.Completed,
                null,
                null)
        ];
        var queries = new FakeDeadlineReadQueries(deadlines);
        ListLegalDeadlinesUseCase useCase = CreateUseCase(role, queries);

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            2,
            10);

        Assert.Equal(ListLegalDeadlinesResultStatus.Succeeded, result.Status);
        Assert.Equal(deadlines, result.Items);
        Assert.Contains(result.Items, item => item.State == LegalDeadlineReadState.Pending);
        Assert.Contains(result.Items, item => item.State == LegalDeadlineReadState.Completed);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(2, queries.PageNumber);
        Assert.Equal(10, queries.PageSize);
        Assert.Equal(
            LegalDeadlineReadResponsibleFilterKind.Any,
            queries.Request?.ResponsibleFilterKind);
        Assert.Null(queries.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData(null, LegalDeadlineReadResponsibleFilterKind.Any)]
    [InlineData("any", LegalDeadlineReadResponsibleFilterKind.Any)]
    [InlineData("ANY", LegalDeadlineReadResponsibleFilterKind.Any)]
    [InlineData("unassigned", LegalDeadlineReadResponsibleFilterKind.Unassigned)]
    public async Task ExecuteAsync_WithKeywordResponsible_ForwardsKindWithoutMembership(
        string? responsible,
        LegalDeadlineReadResponsibleFilterKind expected)
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            responsible: responsible);

        Assert.Equal(expected, queries.Request?.ResponsibleFilterKind);
        Assert.Null(queries.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData("self", OrganizationRole.Member)]
    [InlineData("Self", OrganizationRole.Owner)]
    public async Task ExecuteAsync_WithSelfResponsible_UsesAuthorizedActorMembership(
        string responsible,
        OrganizationRole role)
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(role, queries);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            responsible: responsible);

        Assert.Equal(
            LegalDeadlineReadResponsibleFilterKind.Membership,
            queries.Request?.ResponsibleFilterKind);
        Assert.Equal(ActorMembershipId, queries.Request?.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMembershipResponsible_ForwardsRequestedMembership()
    {
        Guid membershipId = Guid.Parse("8f9a8a2e-1c34-4b39-9a55-0e7c11d6f8a1");
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            responsible: membershipId.ToString("D"));

        Assert.Equal(
            LegalDeadlineReadResponsibleFilterKind.Membership,
            queries.Request?.ResponsibleFilterKind);
        Assert.Equal(membershipId, queries.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("8f9a8a2e1c344b399a550e7c11d6f8a1")]
    [InlineData("{8f9a8a2e-1c34-4b39-9a55-0e7c11d6f8a1}")]
    [InlineData("")]
    [InlineData("assigned")]
    public async Task ExecuteAsync_WithInvalidResponsible_RejectsBeforeAuthorizationOrQuery(
        string responsible)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    responsible: responsible));

        Assert.Contains("Responsible", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithSelfResponsibleAndDeniedView_DeniesWithoutDeadlineQuery()
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            responsible: "self");

        Assert.Same(ListLegalDeadlinesResult.AccessDenied, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExplicitPagination_UsesBoundedDefaults()
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListLegalDeadlinesUseCase.DefaultPageSize, result.PageSize);
        Assert.Equal(1, queries.PageNumber);
        Assert.Equal(ListLegalDeadlinesUseCase.DefaultPageSize, queries.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaximumPageSize_ForwardsBoundedMaximum()
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            1,
            ListLegalDeadlinesUseCase.MaximumPageSize);

        Assert.Equal(ListLegalDeadlinesUseCase.MaximumPageSize, result.PageSize);
        Assert.Equal(ListLegalDeadlinesUseCase.MaximumPageSize, queries.PageSize);
    }

    [Theory]
    [InlineData(0, 20, "Page number")]
    [InlineData(-1, 20, "Page number")]
    [InlineData(1, 0, "Page size")]
    [InlineData(1, -1, "Page size")]
    [InlineData(1, 101, "Page size")]
    [InlineData(int.MaxValue, 100, "offset")]
    public async Task ExecuteAsync_WithInvalidPagination_RejectsBeforeAuthorizationOrQuery(
        int pageNumber,
        int pageSize,
        string expectedMessage)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    pageNumber,
                    pageSize));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public void ListItemContract_ContainsOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(LegalDeadlineListItem.Id),
                nameof(LegalDeadlineListItem.Title),
                nameof(LegalDeadlineListItem.DueDate),
                nameof(LegalDeadlineListItem.ProcessId),
                nameof(LegalDeadlineListItem.ProcessTitle),
                nameof(LegalDeadlineListItem.ClientName),
                nameof(LegalDeadlineListItem.State),
                nameof(LegalDeadlineListItem.ResponsibleMembershipId),
                nameof(LegalDeadlineListItem.ResponsibleDisplayName)
            ],
            typeof(LegalDeadlineListItem)
                .GetProperties()
                .Select(property => property.Name));
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_ForwardsTokenToQuery()
    {
        var queries = new FakeDeadlineReadQueries();
        ListLegalDeadlinesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            1,
            20,
            cancellationToken: cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static ListLegalDeadlinesUseCase CreateUseCase(
        OrganizationRole? role,
        FakeDeadlineReadQueries queries)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), queries);
    }

    private static ListLegalDeadlinesUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeDeadlineReadQueries queries)
    {
        var authorization = new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(lookup));
        return new ListLegalDeadlinesUseCase(authorization, queries);
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

    private sealed class FakeDeadlineReadQueries(
        IReadOnlyList<LegalDeadlineListItem>? deadlines = null)
        : ILegalDeadlineReadQueries
    {
        public int ListCallCount { get; private set; }

        public LegalDeadlineListReadRequest? Request { get; private set; }

        public Guid OrganizationId { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDeadlineDetailReadModel?> FindAsync(
            Guid deadlineId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "FindAsync must not be called by List Deadline tests.");
        }

        public Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
            LegalDeadlineListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            Request = request;
            OrganizationId = request.OrganizationId;
            PageNumber = request.PageNumber;
            PageSize = request.PageSize;
            CancellationToken = cancellationToken;
            return Task.FromResult(
                deadlines ?? Array.Empty<LegalDeadlineListItem>());
        }
    }
}
