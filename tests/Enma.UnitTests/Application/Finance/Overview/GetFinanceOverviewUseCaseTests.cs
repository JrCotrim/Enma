using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.Overview;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.Overview;

public sealed class GetFinanceOverviewUseCaseTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(
        2026, 9, 7, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedRole_ReturnsOverviewAndForwardsContext(
        OrganizationRole role)
    {
        FinanceOverviewReadModel overview = CreateOverview();
        var queries = new StubFinanceReadQueries(overview);
        var clock = new RecordingTimeProvider(Now);
        GetFinanceOverviewUseCase useCase = CreateUseCase(role, queries, clock);
        using var cancellationSource = new CancellationTokenSource();

        GetFinanceOverviewResult result = await useCase.ExecuteAsync(
            new GetFinanceOverviewQuery(UserId, OrganizationId),
            cancellationSource.Token);

        Assert.Equal(GetFinanceOverviewResultStatus.Succeeded, result.Status);
        Assert.Same(overview, result.Overview);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(new DateOnly(2026, 9, 7), queries.ReferenceDate);
        Assert.Equal(cancellationSource.Token, queries.CancellationToken);
        Assert.Equal(1, queries.GetOverviewCallCount);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Member_DeniesBeforeClockAndQuery()
    {
        var queries = new StubFinanceReadQueries(CreateOverview());
        var clock = new RecordingTimeProvider(Now);
        GetFinanceOverviewUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries,
            clock);

        GetFinanceOverviewResult result = await useCase.ExecuteAsync(
            new GetFinanceOverviewQuery(UserId, OrganizationId));

        Assert.Same(GetFinanceOverviewResult.AccessDenied, result);
        Assert.Equal(0, queries.GetOverviewCallCount);
        Assert.Equal(0, clock.CallCount);
    }

    private static FinanceOverviewReadModel CreateOverview() => new(
        new DateOnly(2026, 9, 7),
        100.01m,
        25.01m,
        75m,
        25m,
        25m,
        25m,
        2,
        1,
        1,
        1,
        1,
        1);

    private static GetFinanceOverviewUseCase CreateUseCase(
        OrganizationRole role,
        StubFinanceReadQueries queries,
        RecordingTimeProvider clock) => new(
            new FinanceActionAuthorization(
                new OrganizationAccessAuthorization(
                    new StubOrganizationAccessLookup(role))),
            queries,
            clock);

    private sealed class StubOrganizationAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationRole?>(role);

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationAccessLookupResult?>(
                new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    MembershipId,
                    role));
    }

    private sealed class StubFinanceReadQueries(FinanceOverviewReadModel overview)
        : IFinanceReadQueries
    {
        public int GetOverviewCallCount { get; private set; }
        public Guid OrganizationId { get; private set; }
        public DateOnly ReferenceDate { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<FinanceOverviewReadModel> GetOverviewAsync(
            Guid organizationId,
            DateOnly referenceDate,
            CancellationToken cancellationToken = default)
        {
            GetOverviewCallCount++;
            OrganizationId = organizationId;
            ReferenceDate = referenceDate;
            CancellationToken = cancellationToken;
            return Task.FromResult(overview);
        }

        public Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
            Guid organizationId,
            Guid? clientId,
            DateOnly referenceDate,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaymentPlanListItemReadModel>>([]);

        public Task<PaymentPlanDetailReadModel?> FindAsync(
            Guid organizationId,
            Guid paymentPlanId,
            DateOnly referenceDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PaymentPlanDetailReadModel?>(null);
    }

    private sealed class RecordingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return utcNow;
        }
    }
}
