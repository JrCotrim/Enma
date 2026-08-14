using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Tasks.GetById;

public sealed class GetLegalTaskUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "7d0a71c1-7db0-4c5d-9db0-a7fdddc3333a");
    private static readonly Guid OrganizationId = Guid.Parse(
        "b03a8fe9-171b-4c87-873a-c901ec17342e");
    private static readonly Guid MembershipId = Guid.Parse(
        "dc782f51-858a-459b-b8cc-e59e8f604a9e");
    private static readonly Guid LegalTaskId = Guid.Parse(
        "f5afce35-af12-4922-8d96-00d68f4807dd");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutTaskQuery()
    {
        var queries = new FakeReadQueries();
        GetLegalTaskUseCase useCase = CreateUseCase(null, queries);

        GetLegalTaskResult result = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(UserId, OrganizationId, LegalTaskId));

        Assert.Same(GetLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyTaskId_ReturnsInvalidInputWithoutTaskQuery()
    {
        var queries = new FakeReadQueries();
        GetLegalTaskUseCase useCase = CreateUseCase(OrganizationRole.Member, queries);

        GetLegalTaskResult result = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(UserId, OrganizationId, Guid.Empty));

        Assert.Same(GetLegalTaskResult.InvalidInput, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingOrCrossTenantTask_ReturnsSameNotFound()
    {
        GetLegalTaskUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeReadQueries());

        GetLegalTaskResult missing = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(UserId, OrganizationId, Guid.NewGuid()));
        GetLegalTaskResult crossTenant = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(UserId, OrganizationId, LegalTaskId));

        Assert.Same(GetLegalTaskResult.NotFound, missing);
        Assert.Same(missing, crossTenant);
    }

    [Fact]
    public async Task ExecuteAsync_WithTask_ReturnsApprovedDetailAndTenantScope()
    {
        var expected = new LegalTaskDetailReadModel(
            LegalTaskId,
            "Prepare hearing",
            "Review evidence",
            new DateOnly(2026, 9, 3),
            null,
            null,
            null,
            null,
            null,
            MembershipId,
            "Creator Name",
            LegalTaskState.Pending,
            new DateTimeOffset(2026, 8, 14, 20, 0, 0, TimeSpan.Zero),
            null);
        var queries = new FakeReadQueries(expected);
        GetLegalTaskUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        GetLegalTaskResult result = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(UserId, OrganizationId, LegalTaskId),
            cancellationTokenSource.Token);

        Assert.Equal(GetLegalTaskResultStatus.Succeeded, result.Status);
        Assert.Equal(expected, result.LegalTask);
        Assert.Equal(LegalTaskId, queries.LegalTaskId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    [Fact]
    public void DetailContract_ContainsOnlyApprovedFieldsAndStates()
    {
        Assert.Equal(
            [
                nameof(LegalTaskDetailReadModel.Id),
                nameof(LegalTaskDetailReadModel.Title),
                nameof(LegalTaskDetailReadModel.Description),
                nameof(LegalTaskDetailReadModel.DueDate),
                nameof(LegalTaskDetailReadModel.ProcessId),
                nameof(LegalTaskDetailReadModel.ProcessTitle),
                nameof(LegalTaskDetailReadModel.ClientName),
                nameof(LegalTaskDetailReadModel.AssigneeMembershipId),
                nameof(LegalTaskDetailReadModel.AssigneeDisplayName),
                nameof(LegalTaskDetailReadModel.CreatedByMembershipId),
                nameof(LegalTaskDetailReadModel.CreatedByDisplayName),
                nameof(LegalTaskDetailReadModel.State),
                nameof(LegalTaskDetailReadModel.CreatedAt),
                nameof(LegalTaskDetailReadModel.CompletedAt)
            ],
            typeof(LegalTaskDetailReadModel)
                .GetProperties()
                .Select(property => property.Name));
        Assert.Equal(
            [nameof(LegalTaskState.Pending), nameof(LegalTaskState.Completed)],
            Enum.GetNames<LegalTaskState>());
    }

    private static GetLegalTaskUseCase CreateUseCase(
        OrganizationRole? role,
        FakeReadQueries queries)
    {
        var viewAuthorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(
                new StubAccessLookup(role)));
        return new GetLegalTaskUseCase(viewAuthorization, queries);
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
                    MembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeReadQueries(
        LegalTaskDetailReadModel? legalTask = null) : ILegalTaskReadQueries
    {
        public int FindCallCount { get; private set; }
        public Guid LegalTaskId { get; private set; }
        public Guid OrganizationId { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalTaskDetailReadModel?> FindAsync(
            Guid legalTaskId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            LegalTaskId = legalTaskId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(legalTask);
        }

        public Task<LegalTaskListReadPage> ListAsync(
            LegalTaskListReadRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }
    }
}
