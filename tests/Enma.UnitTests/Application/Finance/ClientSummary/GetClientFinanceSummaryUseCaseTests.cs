using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.ClientSummary;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.ClientSummary;

public sealed class GetClientFinanceSummaryUseCaseTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(
        2026, 9, 7, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedRole_ReturnsSummaryAndForwardsContext(
        OrganizationRole role)
    {
        ClientFinanceSummaryReadModel summary = CreateSummary();
        var queries = new StubFinanceReadQueries(summary);
        var clock = new RecordingTimeProvider(Now);
        GetClientFinanceSummaryUseCase useCase = CreateUseCase(role, queries, clock);
        using var cancellationSource = new CancellationTokenSource();

        GetClientFinanceSummaryResult result = await useCase.ExecuteAsync(
            new GetClientFinanceSummaryQuery(UserId, OrganizationId, ClientId),
            cancellationSource.Token);

        Assert.Equal(GetClientFinanceSummaryResultStatus.Succeeded, result.Status);
        Assert.Same(summary, result.Summary);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(ClientId, queries.ClientId);
        Assert.Equal(new DateOnly(2026, 9, 7), queries.ReferenceDate);
        Assert.Equal(cancellationSource.Token, queries.CancellationToken);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MissingClient_ReturnsNotFound()
    {
        var queries = new StubFinanceReadQueries(null);
        var clock = new RecordingTimeProvider(Now);
        GetClientFinanceSummaryUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            clock);

        GetClientFinanceSummaryResult result = await useCase.ExecuteAsync(
            new GetClientFinanceSummaryQuery(UserId, OrganizationId, ClientId));

        Assert.Same(GetClientFinanceSummaryResult.NotFound, result);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroSummary_IsReturnedUnchanged()
    {
        var summary = new ClientFinanceSummaryReadModel(
            ClientId,
            new DateOnly(2026, 9, 7),
            0m,
            0m,
            0m,
            0m,
            0L);
        var queries = new StubFinanceReadQueries(summary);
        var clock = new RecordingTimeProvider(Now);
        GetClientFinanceSummaryUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries,
            clock);

        GetClientFinanceSummaryResult result = await useCase.ExecuteAsync(
            new GetClientFinanceSummaryQuery(UserId, OrganizationId, ClientId));

        Assert.Equal(GetClientFinanceSummaryResultStatus.Succeeded, result.Status);
        Assert.Same(summary, result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_Member_DeniesBeforeClockAndQuery()
    {
        var queries = new StubFinanceReadQueries(CreateSummary());
        var clock = new RecordingTimeProvider(Now);
        GetClientFinanceSummaryUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries,
            clock);

        GetClientFinanceSummaryResult result = await useCase.ExecuteAsync(
            new GetClientFinanceSummaryQuery(UserId, OrganizationId, ClientId));

        Assert.Same(GetClientFinanceSummaryResult.AccessDenied, result);
        Assert.Equal(0, queries.CallCount);
        Assert.Equal(0, clock.CallCount);
    }

    private static ClientFinanceSummaryReadModel CreateSummary() => new(
        ClientId,
        new DateOnly(2026, 9, 7),
        120.01m,
        45.01m,
        75m,
        25m,
        2L);

    private static GetClientFinanceSummaryUseCase CreateUseCase(
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

    private sealed class StubFinanceReadQueries(
        ClientFinanceSummaryReadModel? summary) : IFinanceReadQueries
    {
        public int CallCount { get; private set; }
        public Guid OrganizationId { get; private set; }
        public Guid ClientId { get; private set; }
        public DateOnly ReferenceDate { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<FinanceOverviewReadModel> GetOverviewAsync(
            Guid organizationId,
            DateOnly referenceDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClientFinanceSummaryReadModel?> GetClientSummaryAsync(
            Guid organizationId,
            Guid clientId,
            DateOnly referenceDate,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationId = organizationId;
            ClientId = clientId;
            ReferenceDate = referenceDate;
            CancellationToken = cancellationToken;
            return Task.FromResult(summary);
        }

        public Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
            Guid organizationId,
            Guid? clientId,
            DateOnly referenceDate,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentPlanDetailReadModel?> FindAsync(
            Guid organizationId,
            Guid paymentPlanId,
            DateOnly referenceDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
