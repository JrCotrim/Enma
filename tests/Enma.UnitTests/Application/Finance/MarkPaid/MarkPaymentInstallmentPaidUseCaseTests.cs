using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.MarkPaid;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.MarkPaid;

public sealed class MarkPaymentInstallmentPaidUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "56f48ba7-1769-4595-989b-d2b869c15f78");

    private static readonly Guid OrganizationId = Guid.Parse(
        "eb004939-8838-4c57-b4ed-bbc4055de31b");

    private static readonly Guid MembershipId = Guid.Parse(
        "9c879d82-5826-4f12-ab2a-344cc297ad7e");

    private static readonly Guid ClientId = Guid.Parse(
        "a838489c-c28d-4621-bf1c-45e07affcfbb");

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset PaidAt =
        CreatedAt.AddHours(2);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_UsesServerTimestamp(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence(role);
        var timeProvider = new CountingTimeProvider(PaidAt);
        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            role,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.Succeeded, result);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforeTimeAndPersistence()
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Member);
        var timeProvider = new CountingTimeProvider(PaidAt);
        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
        Assert.Null(persistence.Installment.PaidAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_WithEmptyResourceId_ReturnsNotFoundWithoutPersistence(
        bool emptyPlanId,
        bool emptyInstallmentId)
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Owner);
        var timeProvider = new CountingTimeProvider(PaidAt);
        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            timeProvider);

        var command = new MarkPaymentInstallmentPaidCommand(
            UserId,
            OrganizationId,
            emptyPlanId ? Guid.Empty : persistence.PaymentPlanId,
            emptyInstallmentId ? Guid.Empty : persistence.Installment.Id);

        MarkPaymentInstallmentPaidResult result =
            await useCase.ExecuteAsync(command);

        Assert.Equal(MarkPaymentInstallmentPaidResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceCannotFindResource_ReturnsNotFound()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            PaymentInstallmentMutationPersistenceResult.NotFound);

        var timeProvider = new CountingTimeProvider(PaidAt);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.NotFound, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
        Assert.Null(persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMembershipBecomesInactive_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Owner)
        {
            IsMembershipActive = false
        };

        var timeProvider = new CountingTimeProvider(PaidAt);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
        Assert.Null(persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRoleIsDemotedAfterPrecheck_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Member);
        var timeProvider = new CountingTimeProvider(PaidAt);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
        Assert.Null(persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyPaid_PreservesFirstTimestampWithoutReadingClock()
    {
        DateTimeOffset firstPaidAt = CreatedAt.AddHours(1);

        var persistence = new FakeMutationPersistence(
            OrganizationRole.Administrator);

        persistence.Installment.MarkPaid(firstPaidAt);

        var timeProvider = new CountingTimeProvider(PaidAt);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence,
            timeProvider);

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence));

        Assert.Equal(MarkPaymentInstallmentPaidResult.Succeeded, result);
        Assert.Equal(firstPaidAt, persistence.Installment.PaidAt);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithServerClockBeforeCreation_PropagatesInvariantFailure()
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Owner);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            new CountingTimeProvider(CreatedAt.AddTicks(-1)));

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                useCase.ExecuteAsync(CreateCommand(persistence)));

        Assert.Equal("paidAt", exception.ParamName);
        Assert.Null(persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsTenantResourceIdsAndCancellation()
    {
        var persistence = new FakeMutationPersistence(OrganizationRole.Owner);

        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);

        MarkPaymentInstallmentPaidUseCase useCase = CreateUseCase(
            lookup,
            persistence,
            new CountingTimeProvider(PaidAt));

        using var cancellation = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            CreateCommand(persistence),
            cancellation.Token);

        Assert.Equal(cancellation.Token, lookup.CancellationToken);
        Assert.Equal(cancellation.Token, persistence.CancellationToken);
        Assert.Equal(UserId, persistence.UserId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(MembershipId, persistence.ActorMembershipId);
        Assert.Equal(persistence.PaymentPlanId, persistence.RequestPaymentPlanId);
        Assert.Equal(persistence.Installment.Id, persistence.RequestInstallmentId);
    }

    private static MarkPaymentInstallmentPaidCommand CreateCommand(
        FakeMutationPersistence persistence)
    {
        return new MarkPaymentInstallmentPaidCommand(
            UserId,
            OrganizationId,
            persistence.PaymentPlanId,
            persistence.Installment.Id);
    }

    private static MarkPaymentInstallmentPaidUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence,
        TimeProvider timeProvider)
    {
        return CreateUseCase(
            new ContextualAccessLookup(OrganizationId, role),
            persistence,
            timeProvider);
    }

    private static MarkPaymentInstallmentPaidUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeMutationPersistence persistence,
        TimeProvider timeProvider)
    {
        return new MarkPaymentInstallmentPaidUseCase(
            new FinanceActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence,
            timeProvider);
    }

    private sealed class ContextualAccessLookup(
        Guid organizationId,
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid requestedOrganizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;

            return Task.FromResult(
                requestedOrganizationId == organizationId
                    ? role
                    : null);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid requestedOrganizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;

            OrganizationAccessLookupResult? result =
                requestedOrganizationId == organizationId &&
                role.HasValue
                    ? new OrganizationAccessLookupResult(
                        userId,
                        requestedOrganizationId,
                        MembershipId,
                        role.Value)
                    : null;

            return Task.FromResult(result);
        }
    }

    private sealed class FakeMutationPersistence
        : IPaymentInstallmentMutationPersistence
    {
        private readonly PaymentInstallmentMutationPersistenceResult _result;

        public FakeMutationPersistence(
            OrganizationRole actorRole,
            PaymentInstallmentMutationPersistenceResult result =
                PaymentInstallmentMutationPersistenceResult.Succeeded)
        {
            ActorRole = actorRole;
            _result = result;

            var paymentPlan = new ClientPaymentPlan(
                MarkPaymentInstallmentPaidUseCaseTests.OrganizationId,
                ClientId,
                100m,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt);

            PaymentPlanId = paymentPlan.Id;
            Installment = Assert.Single(paymentPlan.Installments);
        }

        public OrganizationRole ActorRole { get; set; }

        public bool IsMembershipActive { get; set; } = true;

        public bool IsUserActive { get; set; } = true;

        public bool IsOrganizationActive { get; set; } = true;

        public Guid PaymentPlanId { get; }

        public PaymentInstallment Installment { get; }

        public int CallCount { get; private set; }

        public Guid UserId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public Guid ActorMembershipId { get; private set; }

        public Guid RequestPaymentPlanId { get; private set; }

        public Guid RequestInstallmentId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<PaymentInstallmentMutationPersistenceResult> ExecuteAsync(
            PaymentInstallmentMutationPersistenceRequest request,
            Func<
                PaymentInstallmentMutationLockedState,
                PaymentInstallmentMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = request.UserId;
            OrganizationId = request.OrganizationId;
            ActorMembershipId = request.ActorMembershipId;
            RequestPaymentPlanId = request.PaymentPlanId;
            RequestInstallmentId = request.InstallmentId;
            CancellationToken = cancellationToken;

            if (_result != PaymentInstallmentMutationPersistenceResult.Succeeded)
            {
                return Task.FromResult(_result);
            }

            PaymentInstallmentMutationDecision decision = decide(
                new PaymentInstallmentMutationLockedState(
                    Installment,
                    IsOrganizationActive,
                    new PaymentInstallmentMutationActorState(
                        MembershipId,
                        request.OrganizationId,
                        request.UserId,
                        ActorRole,
                        IsMembershipActive,
                        IsUserActive)));

            return Task.FromResult(
                decision.Status ==
                    PaymentInstallmentMutationDecisionStatus.AccessDenied
                    ? PaymentInstallmentMutationPersistenceResult.AccessDenied
                    : PaymentInstallmentMutationPersistenceResult.Succeeded);
        }
    }

    private sealed class CountingTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;
            return utcNow;
        }
    }
}
