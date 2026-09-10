using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.List;

public sealed class ListPaymentPlansUseCaseTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedRole_ReturnsPagedItems(OrganizationRole role)
    {
        var queries = new StubFinanceReadQueries([CreateItem(), CreateItem(), CreateItem()]);
        var clock = new RecordingTimeProvider(Now);
        ListPaymentPlansUseCase useCase = CreateUseCase(role, queries, clock);

        ListPaymentPlansResult result = await useCase.ExecuteAsync(
            new ListPaymentPlansQuery(UserId, OrganizationId, ClientId, 2, 2));

        Assert.Equal(ListPaymentPlansResultStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(2, result.PageSize);
        Assert.True(result.HasNext);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(ClientId, queries.ClientId);
        Assert.Equal(new DateOnly(2026, 9, 7), queries.ReferenceDate);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Member_DeniesBeforeClockAndQuery()
    {
        var queries = new StubFinanceReadQueries([]);
        var clock = new RecordingTimeProvider(Now);
        ListPaymentPlansUseCase useCase = CreateUseCase(OrganizationRole.Member, queries, clock);

        ListPaymentPlansResult result = await useCase.ExecuteAsync(
            new ListPaymentPlansQuery(UserId, OrganizationId));

        Assert.Same(ListPaymentPlansResult.AccessDenied, result);
        Assert.Equal(0, queries.ListCallCount);
        Assert.Equal(0, clock.CallCount);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(2147483647, 100)]
    public async Task ExecuteAsync_InvalidPagination_ThrowsBeforeQuery(int pageNumber, int pageSize)
    {
        var queries = new StubFinanceReadQueries([]);
        ListPaymentPlansUseCase useCase = CreateUseCase(
            OrganizationRole.Owner, queries, new RecordingTimeProvider(Now));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(new ListPaymentPlansQuery(
                UserId, OrganizationId, null, pageNumber, pageSize)));

        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyOptionalClientId_IsInvalid()
    {
        var queries = new StubFinanceReadQueries([]);
        ListPaymentPlansUseCase useCase = CreateUseCase(
            OrganizationRole.Owner, queries, new RecordingTimeProvider(Now));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(new ListPaymentPlansQuery(
                UserId, OrganizationId, Guid.Empty)));

        Assert.Equal(0, queries.ListCallCount);
    }

    private static PaymentPlanListItemReadModel CreateItem() => new(
        Guid.NewGuid(), ClientId, "Client", 10m, 1,
        new DateOnly(2026, 9, 7), Now, 10m, 0, new DateOnly(2026, 9, 7));

    private static ListPaymentPlansUseCase CreateUseCase(
        OrganizationRole role,
        StubFinanceReadQueries queries,
        RecordingTimeProvider clock) => new(
            new FinanceActionAuthorization(
                new OrganizationAccessAuthorization(new StubOrganizationAccessLookup(role))),
            queries,
            clock);

    private sealed class StubOrganizationAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationRole?>(role);

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationAccessLookupResult?>(
                new OrganizationAccessLookupResult(
                    UserId, OrganizationId, MembershipId, role));
    }

    private sealed class StubFinanceReadQueries(IReadOnlyList<PaymentPlanListItemReadModel> items)
        : IFinanceReadQueries
    {
        public int ListCallCount { get; private set; }
        public Guid OrganizationId { get; private set; }
        public Guid? ClientId { get; private set; }
        public DateOnly ReferenceDate { get; private set; }

        public Task<FinanceOverviewReadModel> GetOverviewAsync(
            Guid organizationId, DateOnly referenceDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClientFinanceSummaryReadModel?> GetClientSummaryAsync(
            Guid organizationId, Guid clientId, DateOnly referenceDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
            Guid organizationId, Guid? clientId, DateOnly referenceDate,
            int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            OrganizationId = organizationId;
            ClientId = clientId;
            ReferenceDate = referenceDate;
            return Task.FromResult(items);
        }

        public Task<PaymentPlanDetailReadModel?> FindAsync(
            Guid organizationId, Guid paymentPlanId, DateOnly referenceDate,
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
