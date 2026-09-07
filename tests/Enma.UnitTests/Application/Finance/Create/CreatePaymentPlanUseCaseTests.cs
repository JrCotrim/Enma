using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.Create;
using Enma.Application.Validation;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.Create;

public sealed class CreatePaymentPlanUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "7f9957aa-e675-4c7f-a138-5862feb5887b");
    private static readonly Guid OrganizationId = Guid.Parse(
        "451a67b6-afb1-4a26-a6bb-d181de47f9fc");
    private static readonly Guid MembershipId = Guid.Parse(
        "253e999a-748d-43a6-8223-53ee12735f4f");
    private static readonly Guid ClientId = Guid.Parse(
        "1dd73267-44c4-4be8-9d77-4d74b65f20f0");
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 9, 7, 18, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedRole_CreatesAggregateFromCommand(
        OrganizationRole role)
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(role));
        var timeProvider = new RecordingTimeProvider(CreatedAt);
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            role, persistence, timeProvider);
        var command = new CreatePaymentPlanCommand(
            UserId,
            OrganizationId,
            ClientId,
            100.01m,
            3,
            new DateOnly(2027, 1, 31));

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(command);

        ClientPaymentPlan paymentPlan = Assert.IsType<ClientPaymentPlan>(
            persistence.PaymentPlan);
        Assert.Equal(CreatePaymentPlanResultStatus.Succeeded, result.Status);
        Assert.Equal(paymentPlan.Id, result.PaymentPlanId);
        Assert.Equal(UserId, persistence.Request?.UserId);
        Assert.Equal(OrganizationId, persistence.Request?.OrganizationId);
        Assert.Equal(MembershipId, persistence.Request?.ActorMembershipId);
        Assert.Equal(ClientId, persistence.Request?.ClientId);
        Assert.Equal(OrganizationId, paymentPlan.OrganizationId);
        Assert.Equal(ClientId, paymentPlan.ClientId);
        Assert.Equal(100.01m, paymentPlan.TotalAmount);
        Assert.Equal(3, paymentPlan.InstallmentCount);
        Assert.Equal(new DateOnly(2027, 1, 31), paymentPlan.FirstDueDate);
        Assert.Equal(CreatedAt, paymentPlan.CreatedAt);
        Assert.Equal(
            [33.34m, 33.34m, 33.33m],
            paymentPlan.Installments.Select(item => item.Amount));
        Assert.Equal(
            [
                new DateOnly(2027, 1, 31),
                new DateOnly(2027, 2, 28),
                new DateOnly(2027, 3, 31)
            ],
            paymentPlan.Installments.Select(item => item.DueDate));
        Assert.Equal(1, timeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Member_DeniesBeforePersistence()
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(OrganizationRole.Owner));
        var timeProvider = new RecordingTimeProvider(CreatedAt);
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Member, persistence, timeProvider);

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(
            ValidCommand());

        Assert.Same(CreatePaymentPlanResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(0, timeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_TransactionRoleDemoted_DeniesWithoutCreation()
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(OrganizationRole.Member));
        var timeProvider = new RecordingTimeProvider(CreatedAt);
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner, persistence, timeProvider);

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(
            ValidCommand());

        Assert.Same(CreatePaymentPlanResult.AccessDenied, result);
        Assert.Null(persistence.PaymentPlan);
        Assert.Equal(0, timeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableClient_ReturnsGenericNotFoundResult()
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(
                OrganizationRole.Owner,
                isClientAvailable: false));
        var timeProvider = new RecordingTimeProvider(CreatedAt);
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner, persistence, timeProvider);

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(
            ValidCommand());

        Assert.Same(CreatePaymentPlanResult.RelatedClientUnavailable, result);
        Assert.Null(persistence.PaymentPlan);
        Assert.Equal(0, timeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyClientId_IsInvalidBeforePersistence()
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(OrganizationRole.Owner));
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            new RecordingTimeProvider(CreatedAt));
        CreatePaymentPlanCommand command = ValidCommand() with
        {
            ClientId = Guid.Empty
        };

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(command));

        Assert.Contains(FinanceErrors.ClientIdRequired, exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    public static TheoryData<decimal, int, DateOnly, string> InvalidInputs =>
        new()
        {
            { 0m, 1, new DateOnly(2027, 1, 1), FinanceErrors.TotalAmountInvalid },
            { 1.001m, 1, new DateOnly(2027, 1, 1), FinanceErrors.TotalAmountScaleInvalid },
            { 10_000_000_000_000_000m, 1, new DateOnly(2027, 1, 1), FinanceErrors.TotalAmountInvalid },
            { 1m, 0, new DateOnly(2027, 1, 1), FinanceErrors.InstallmentCountInvalid },
            { 121m, 121, new DateOnly(2027, 1, 1), FinanceErrors.InstallmentCountInvalid },
            { 0.01m, 2, new DateOnly(2027, 1, 1), FinanceErrors.InstallmentCountExceedsAmount },
            { 1m, 1, DateOnly.MinValue, FinanceErrors.FirstDueDateInvalid },
            { 2m, 2, DateOnly.MaxValue, FinanceErrors.ScheduleOutOfRange }
        };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task ExecuteAsync_InvalidDomainInput_UsesRequestValidationContract(
        decimal totalAmount,
        int installmentCount,
        DateOnly firstDueDate,
        string expectedError)
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(OrganizationRole.Owner));
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            new RecordingTimeProvider(CreatedAt));
        CreatePaymentPlanCommand command = ValidCommand() with
        {
            TotalAmount = totalAmount,
            InstallmentCount = installmentCount,
            FirstDueDate = firstDueDate
        };

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(command));

        Assert.Contains(expectedError, exception.Message);
        Assert.Equal(1, persistence.CallCount);
        Assert.Null(persistence.PaymentPlan);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCancellationToken()
    {
        var persistence = new StubPaymentPlanCreationPersistence(
            CreateLockedState(OrganizationRole.Owner));
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            new RecordingTimeProvider(CreatedAt));
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            ValidCommand(),
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            persistence.CancellationToken);
    }

    private static CreatePaymentPlanCommand ValidCommand()
    {
        return new CreatePaymentPlanCommand(
            UserId,
            OrganizationId,
            ClientId,
            100m,
            2,
            new DateOnly(2027, 1, 1));
    }

    private static PaymentPlanCreationLockedState CreateLockedState(
        OrganizationRole role,
        bool isClientAvailable = true)
    {
        return new PaymentPlanCreationLockedState(
            IsOrganizationActive: true,
            new PaymentPlanCreationActorState(
                MembershipId,
                OrganizationId,
                UserId,
                role,
                IsMembershipActive: true,
                IsUserActive: true),
            isClientAvailable);
    }

    private static CreatePaymentPlanUseCase CreateUseCase(
        OrganizationRole role,
        StubPaymentPlanCreationPersistence persistence,
        RecordingTimeProvider timeProvider)
    {
        var actionAuthorization = new FinanceActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));

        return new CreatePaymentPlanUseCase(
            actionAuthorization,
            persistence,
            timeProvider);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationAccessLookupResult?>(
                new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    MembershipId,
                    role));
        }
    }

    private sealed class StubPaymentPlanCreationPersistence(
        PaymentPlanCreationLockedState lockedState)
        : IPaymentPlanCreationPersistence
    {
        public int CallCount { get; private set; }
        public PaymentPlanCreationPersistenceRequest? Request { get; private set; }
        public ClientPaymentPlan? PaymentPlan { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<PaymentPlanCreationPersistenceResult> ExecuteAsync(
            PaymentPlanCreationPersistenceRequest request,
            Func<PaymentPlanCreationLockedState, PaymentPlanCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            PaymentPlanCreationDecision decision = decide(lockedState);
            PaymentPlan = decision.PaymentPlan;

            return Task.FromResult(
                decision.Status == PaymentPlanCreationDecisionStatus.Persist
                    ? PaymentPlanCreationPersistenceResult.Created(
                        Assert.IsType<ClientPaymentPlan>(PaymentPlan).Id)
                    : PaymentPlanCreationPersistenceResult.Rejected(
                        decision.Status));
        }
    }

    private sealed class RecordingTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return utcNow;
        }
    }
}
