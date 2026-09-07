using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.GetById;

public sealed class GetPaymentPlanUseCaseTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid PaymentPlanId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedRole_ReturnsPlan(OrganizationRole role)
    {
        PaymentPlanDetailReadModel plan = CreatePlan();
        var queries = new StubFinanceReadQueries(plan);
        var clock = new RecordingTimeProvider(Now);
        GetPaymentPlanUseCase useCase = CreateUseCase(role, queries, clock);

        GetPaymentPlanResult result = await useCase.ExecuteAsync(
            new GetPaymentPlanQuery(UserId, OrganizationId, PaymentPlanId));

        Assert.Equal(GetPaymentPlanResultStatus.Succeeded, result.Status);
        Assert.Same(plan, result.PaymentPlan);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(PaymentPlanId, queries.PaymentPlanId);
        Assert.Equal(new DateOnly(2026, 9, 7), queries.ReferenceDate);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Member_DeniesBeforeClockAndQuery()
    {
        var queries = new StubFinanceReadQueries(CreatePlan());
        var clock = new RecordingTimeProvider(Now);
        GetPaymentPlanUseCase useCase = CreateUseCase(OrganizationRole.Member, queries, clock);

        GetPaymentPlanResult result = await useCase.ExecuteAsync(
            new GetPaymentPlanQuery(UserId, OrganizationId, PaymentPlanId));

        Assert.Same(GetPaymentPlanResult.AccessDenied, result);
        Assert.Equal(0, queries.FindCallCount);
        Assert.Equal(0, clock.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPlan_ReturnsNotFound()
    {
        var queries = new StubFinanceReadQueries(null);
        GetPaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner, queries, new RecordingTimeProvider(Now));

        GetPaymentPlanResult result = await useCase.ExecuteAsync(
            new GetPaymentPlanQuery(UserId, OrganizationId, PaymentPlanId));

        Assert.Same(GetPaymentPlanResult.NotFound, result);
    }

    [Theory]
    [InlineData(-1, true, PaymentInstallmentStatus.Paid)]
    [InlineData(-1, false, PaymentInstallmentStatus.Overdue)]
    [InlineData(0, false, PaymentInstallmentStatus.DueToday)]
    [InlineData(1, false, PaymentInstallmentStatus.Upcoming)]
    public void Classify_UsesPaidPrecedenceAndReferenceDateBoundary(
        int dueOffset, bool paid, PaymentInstallmentStatus expected)
    {
        DateOnly referenceDate = new(2026, 9, 7);

        PaymentInstallmentStatus actual = PaymentInstallmentStatusClassifier.Classify(
            referenceDate.AddDays(dueOffset), paid ? Now : null, referenceDate);

        Assert.Equal(expected, actual);
    }

    private static PaymentPlanDetailReadModel CreatePlan() => new(
        PaymentPlanId, Guid.NewGuid(), "Client", 10m, 1,
        new DateOnly(2026, 9, 7), Now, new DateOnly(2026, 9, 7), []);

    private static GetPaymentPlanUseCase CreateUseCase(
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

    private sealed class StubFinanceReadQueries(PaymentPlanDetailReadModel? paymentPlan)
        : IFinanceReadQueries
    {
        public int FindCallCount { get; private set; }
        public Guid OrganizationId { get; private set; }
        public Guid PaymentPlanId { get; private set; }
        public DateOnly ReferenceDate { get; private set; }

        public Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
            Guid organizationId, Guid? clientId, DateOnly referenceDate,
            int pageNumber, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaymentPlanListItemReadModel>>([]);

        public Task<PaymentPlanDetailReadModel?> FindAsync(
            Guid organizationId, Guid paymentPlanId, DateOnly referenceDate,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            OrganizationId = organizationId;
            PaymentPlanId = paymentPlanId;
            ReferenceDate = referenceDate;
            return Task.FromResult(paymentPlan);
        }
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
