using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.ReversePayment;
using Enma.Application.Validation;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Finance.ReversePayment;

public sealed class ReverseInstallmentPaymentUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "0f4f1d0e-5a3c-4f55-9a43-9f2b7a7c1d11");

    private static readonly Guid OrganizationId = Guid.Parse(
        "4c3e2a8b-8f61-4d1a-b3c2-6a5e9d0f7b22");

    private static readonly Guid MembershipId = Guid.Parse(
        "b8d1c6e4-2f7a-4e93-8c15-3d9a0e6f4c33");

    private static readonly Guid ClientId = Guid.Parse(
        "e2a7f9c1-6b4d-4a08-9e3f-1c8b5d2a7e44");

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset PaidAt =
        CreatedAt.AddHours(2);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_ReversesPaidInstallment(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence(role, paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            role,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "registeredByMistake"));

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, result);
        Assert.Null(persistence.Installment.PaidAt);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(
            PaymentReversalReason.RegisteredByMistake,
            persistence.Reason);
        Assert.Equal(
            PaymentInstallmentMutationDecisionStatus.Persist,
            persistence.DecisionStatus);
    }

    [Theory]
    [InlineData("registeredByMistake", PaymentReversalReason.RegisteredByMistake)]
    [InlineData("wrongInstallment", PaymentReversalReason.WrongInstallment)]
    [InlineData("paymentNotCompleted", PaymentReversalReason.PaymentNotCompleted)]
    [InlineData("other", PaymentReversalReason.Other)]
    public async Task ExecuteAsync_WithSupportedReason_ForwardsParsedReason(
        string reason,
        PaymentReversalReason expectedReason)
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, reason));

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, result);
        Assert.Equal(expectedReason, persistence.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("RegisteredByMistake")]
    [InlineData("registered_by_mistake")]
    [InlineData(" other")]
    [InlineData("OTHER")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("refund")]
    public async Task ExecuteAsync_WithMissingOrInvalidReason_RejectsBeforePersistence(
        string? reason)
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(CreateCommand(persistence, reason)));

        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforePersistence()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Member,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRoleAndInvalidReason_DeniesBeforeValidation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Member,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "invalid"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutOrganizationAccess_DeniesBeforePersistence()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            role: null,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_WithEmptyResourceId_ReturnsNotFoundWithoutPersistence(
        bool emptyPlanId,
        bool emptyInstallmentId)
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        var command = new ReverseInstallmentPaymentCommand(
            UserId,
            OrganizationId,
            emptyPlanId ? Guid.Empty : persistence.PaymentPlanId,
            emptyInstallmentId ? Guid.Empty : persistence.Installment.Id,
            "other");

        ReverseInstallmentPaymentResult result =
            await useCase.ExecuteAsync(command);

        Assert.Equal(ReverseInstallmentPaymentResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceCannotFindResource_ReturnsNotFound()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true,
            PaymentInstallmentMutationPersistenceResult.NotFound);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.NotFound, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMembershipBecomesInactive_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true)
        {
            IsMembershipActive = false
        };
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRoleIsDemotedAfterPrecheck_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Member,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserBecomesInactive_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true)
        {
            IsUserActive = false
        };
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOrganizationBecomesInactive_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true)
        {
            IsOrganizationActive = false
        };
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLockedActorIsAnotherMembership_DeniesInsideMutation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true)
        {
            LockedMembershipId = Guid.NewGuid()
        };
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "other"));

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstallmentIsNotPaid_SucceedsAsNoOp()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Administrator,
            paid: false);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        ReverseInstallmentPaymentResult result = await useCase.ExecuteAsync(
            CreateCommand(persistence, "wrongInstallment"));

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, result);
        Assert.Null(persistence.Installment.PaidAt);
        Assert.Equal(1, persistence.CallCount);
        Assert.Equal(
            PaymentInstallmentMutationDecisionStatus.Persist,
            persistence.DecisionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_WithMismatchedLockedInstallment_FailsClosed()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        var command = new ReverseInstallmentPaymentCommand(
            UserId,
            OrganizationId,
            persistence.PaymentPlanId,
            Guid.NewGuid(),
            "other");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(command));

        Assert.Equal(PaidAt, persistence.Installment.PaidAt);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsTenantResourceIdsAndCancellation()
    {
        var persistence = new FakeMutationPersistence(
            OrganizationRole.Owner,
            paid: true);
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        ReverseInstallmentPaymentUseCase useCase = CreateUseCase(
            lookup,
            persistence);

        using var cancellation = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            CreateCommand(persistence, "paymentNotCompleted"),
            cancellation.Token);

        Assert.Equal(cancellation.Token, lookup.CancellationToken);
        Assert.Equal(cancellation.Token, persistence.CancellationToken);
        Assert.Equal(UserId, persistence.UserId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(MembershipId, persistence.ActorMembershipId);
        Assert.Equal(persistence.PaymentPlanId, persistence.RequestPaymentPlanId);
        Assert.Equal(persistence.Installment.Id, persistence.RequestInstallmentId);
    }

    private static ReverseInstallmentPaymentCommand CreateCommand(
        FakeMutationPersistence persistence,
        string? reason)
    {
        return new ReverseInstallmentPaymentCommand(
            UserId,
            OrganizationId,
            persistence.PaymentPlanId,
            persistence.Installment.Id,
            reason);
    }

    private static ReverseInstallmentPaymentUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualAccessLookup(OrganizationId, role),
            persistence);
    }

    private static ReverseInstallmentPaymentUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeMutationPersistence persistence)
    {
        return new ReverseInstallmentPaymentUseCase(
            new FinanceActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
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
            bool paid,
            PaymentInstallmentMutationPersistenceResult result =
                PaymentInstallmentMutationPersistenceResult.Succeeded)
        {
            ActorRole = actorRole;
            _result = result;

            var paymentPlan = new ClientPaymentPlan(
                ReverseInstallmentPaymentUseCaseTests.OrganizationId,
                ClientId,
                100m,
                1,
                new DateOnly(2026, 10, 10),
                CreatedAt);

            PaymentPlanId = paymentPlan.Id;
            Installment = Assert.Single(paymentPlan.Installments);

            if (paid)
            {
                Installment.MarkPaid(PaidAt);
            }
        }

        public OrganizationRole ActorRole { get; }

        public bool IsMembershipActive { get; init; } = true;

        public bool IsUserActive { get; init; } = true;

        public bool IsOrganizationActive { get; init; } = true;

        public Guid LockedMembershipId { get; init; } = MembershipId;

        public Guid PaymentPlanId { get; }

        public PaymentInstallment Installment { get; }

        public int CallCount { get; private set; }

        public PaymentReversalReason? Reason { get; private set; }

        public PaymentInstallmentMutationDecisionStatus? DecisionStatus
        {
            get;
            private set;
        }

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
            throw new InvalidOperationException(
                "Payment reversal must never mark an installment as paid.");
        }

        public Task<PaymentInstallmentMutationPersistenceResult> ReversePaymentAsync(
            PaymentInstallmentMutationPersistenceRequest request,
            PaymentReversalReason reason,
            Func<
                PaymentInstallmentMutationLockedState,
                PaymentInstallmentMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Reason = reason;
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
                        LockedMembershipId,
                        request.OrganizationId,
                        request.UserId,
                        ActorRole,
                        IsMembershipActive,
                        IsUserActive)));

            DecisionStatus = decision.Status;

            return Task.FromResult(
                decision.Status ==
                    PaymentInstallmentMutationDecisionStatus.AccessDenied
                    ? PaymentInstallmentMutationPersistenceResult.AccessDenied
                    : PaymentInstallmentMutationPersistenceResult.Succeeded);
        }
    }
}
