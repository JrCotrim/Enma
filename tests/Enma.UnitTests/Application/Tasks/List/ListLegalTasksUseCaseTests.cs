using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.List;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Tasks.List;

public sealed class ListLegalTasksUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "df467717-eef2-4991-bc25-89905bf91991");
    private static readonly Guid OrganizationId = Guid.Parse(
        "4f2ad002-f862-49c1-bb12-6abeb2ac4ce7");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "07e8fc6a-972d-41da-81d3-e6ba72166a80");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "ea8d5928-8018-4c16-a592-c795118c2544");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutTaskQuery()
    {
        var queries = new FakeReadQueries();
        ListLegalTasksUseCase useCase = CreateUseCase(null, queries);

        ListLegalTasksResult result = await useCase.ExecuteAsync(
            new ListLegalTasksQuery(UserId, OrganizationId));

        Assert.Same(ListLegalTasksResult.AccessDenied, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDefaults_UsesPendingAnyAndBoundedPagination()
    {
        var queries = new FakeReadQueries(hasNext: true);
        ListLegalTasksUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalTasksResult result = await useCase.ExecuteAsync(
            new ListLegalTasksQuery(UserId, OrganizationId));

        Assert.Equal(ListLegalTasksResultStatus.Succeeded, result.Status);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListLegalTasksUseCase.DefaultPageSize, result.PageSize);
        Assert.True(result.HasNext);
        Assert.Equal(LegalTaskState.Pending, queries.Request?.State);
        Assert.Equal(LegalTaskReadAssigneeFilterKind.Any,
            queries.Request?.AssigneeFilterKind);
        Assert.Null(queries.Request?.ProcessId);
        Assert.Null(queries.Request?.AssigneeMembershipId);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithSelf_UsesLiveContextualMembershipId(
        OrganizationRole role)
    {
        var queries = new FakeReadQueries();
        ListLegalTasksUseCase useCase = CreateUseCase(
            role,
            queries);

        await useCase.ExecuteAsync(new ListLegalTasksQuery(
            UserId,
            OrganizationId,
            Assignee: LegalTaskAssigneeFilter.Self));

        Assert.Equal(
            LegalTaskReadAssigneeFilterKind.Membership,
            queries.Request?.AssigneeFilterKind);
        Assert.Equal(ActorMembershipId, queries.Request?.AssigneeMembershipId);
        Assert.NotEqual(UserId, queries.Request?.AssigneeMembershipId);
    }

    [Theory]
    [InlineData(LegalTaskAssigneeFilterKind.Any,
        LegalTaskReadAssigneeFilterKind.Any, false)]
    [InlineData(LegalTaskAssigneeFilterKind.Unassigned,
        LegalTaskReadAssigneeFilterKind.Unassigned, false)]
    [InlineData(LegalTaskAssigneeFilterKind.Membership,
        LegalTaskReadAssigneeFilterKind.Membership, true)]
    public async Task ExecuteAsync_WithAssigneeFilter_MapsExactTypedFilter(
        LegalTaskAssigneeFilterKind inputKind,
        LegalTaskReadAssigneeFilterKind expectedKind,
        bool hasMembership)
    {
        var queries = new FakeReadQueries();
        ListLegalTasksUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);
        var assignee = new LegalTaskAssigneeFilter(
            inputKind,
            hasMembership ? OtherMembershipId : null);

        await useCase.ExecuteAsync(new ListLegalTasksQuery(
            UserId,
            OrganizationId,
            LegalTaskState.Completed,
            Guid.Parse("5ba53844-208d-452c-a343-1ca7940f8677"),
            assignee,
            2,
            10));

        Assert.Equal(expectedKind, queries.Request?.AssigneeFilterKind);
        Assert.Equal(
            hasMembership ? OtherMembershipId : null,
            queries.Request?.AssigneeMembershipId);
        Assert.Equal(LegalTaskState.Completed, queries.Request?.State);
        Assert.Equal(2, queries.Request?.PageNumber);
        Assert.Equal(10, queries.Request?.PageSize);
    }

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task ExecuteAsync_WithInvalidInput_ReturnsControlledResultWithoutTaskQuery(
        ListLegalTasksQuery query)
    {
        var queries = new FakeReadQueries();
        ListLegalTasksUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        ListLegalTasksResult result = await useCase.ExecuteAsync(query);

        Assert.Same(ListLegalTasksResult.InvalidInput, result);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public void ListContract_ContainsApprovedFieldsAndNoDescription()
    {
        Assert.Equal(
            [
                nameof(LegalTaskListItem.Id),
                nameof(LegalTaskListItem.Title),
                nameof(LegalTaskListItem.DueDate),
                nameof(LegalTaskListItem.ProcessId),
                nameof(LegalTaskListItem.ProcessTitle),
                nameof(LegalTaskListItem.ClientName),
                nameof(LegalTaskListItem.AssigneeMembershipId),
                nameof(LegalTaskListItem.AssigneeDisplayName),
                nameof(LegalTaskListItem.CreatedByMembershipId),
                nameof(LegalTaskListItem.State),
                nameof(LegalTaskListItem.CreatedAt)
            ],
            typeof(LegalTaskListItem)
                .GetProperties()
                .Select(property => property.Name));
    }

    public static TheoryData<ListLegalTasksQuery> InvalidQueries =>
        new()
        {
            new(UserId, OrganizationId, (LegalTaskState)int.MaxValue),
            new(UserId, OrganizationId, ProcessId: Guid.Empty),
            new(UserId, OrganizationId, Assignee:
                new LegalTaskAssigneeFilter((LegalTaskAssigneeFilterKind)99)),
            new(UserId, OrganizationId, Assignee:
                new LegalTaskAssigneeFilter(
                    LegalTaskAssigneeFilterKind.Any,
                    OtherMembershipId)),
            new(UserId, OrganizationId, Assignee:
                new LegalTaskAssigneeFilter(
                    LegalTaskAssigneeFilterKind.Self,
                    OtherMembershipId)),
            new(UserId, OrganizationId, Assignee:
                new LegalTaskAssigneeFilter(
                    LegalTaskAssigneeFilterKind.Unassigned,
                    OtherMembershipId)),
            new(UserId, OrganizationId, Assignee:
                LegalTaskAssigneeFilter.Membership(Guid.Empty)),
            new(UserId, OrganizationId, Assignee:
                new LegalTaskAssigneeFilter(
                    LegalTaskAssigneeFilterKind.Membership)),
            new(UserId, OrganizationId, PageNumber: 0),
            new(UserId, OrganizationId, PageNumber: -1),
            new(UserId, OrganizationId, PageSize: 0),
            new(UserId, OrganizationId, PageSize: 101),
            new(UserId, OrganizationId, PageNumber: int.MaxValue, PageSize: 100)
        };

    private static ListLegalTasksUseCase CreateUseCase(
        OrganizationRole? role,
        FakeReadQueries queries)
    {
        var authorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(
                new StubAccessLookup(role)));
        return new ListLegalTasksUseCase(authorization, queries);
    }

    private sealed class StubAccessLookup(OrganizationRole? role)
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
            OrganizationAccessLookupResult? result = role.HasValue
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    ActorMembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeReadQueries(bool hasNext = false) : ILegalTaskReadQueries
    {
        public int ListCallCount { get; private set; }
        public LegalTaskListReadRequest? Request { get; private set; }

        public Task<LegalTaskDetailReadModel?> FindAsync(
            Guid legalTaskId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<LegalTaskListReadPage> ListAsync(
            LegalTaskListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            Request = request;
            return Task.FromResult(new LegalTaskListReadPage([], hasNext));
        }
    }
}
