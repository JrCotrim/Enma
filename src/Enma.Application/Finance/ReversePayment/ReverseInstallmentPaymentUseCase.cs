using Enma.Application.Authorization;
using Enma.Domain.Finance;

namespace Enma.Application.Finance.ReversePayment;

public sealed class ReverseInstallmentPaymentUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IPaymentInstallmentMutationPersistence _mutationPersistence;

    public ReverseInstallmentPaymentUseCase(
        FinanceActionAuthorization actionAuthorization,
        IPaymentInstallmentMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ReverseInstallmentPaymentResult> ExecuteAsync(
        ReverseInstallmentPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                command.UserId,
                command.OrganizationId,
                FinanceAction.ReverseInstallmentPayment,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ReverseInstallmentPaymentResult.AccessDenied;
        }

        PaymentReversalReason reason =
            PaymentReversalReasonParser.Parse(command.Reason);

        if (command.PaymentPlanId == Guid.Empty ||
            command.InstallmentId == Guid.Empty)
        {
            return ReverseInstallmentPaymentResult.NotFound;
        }

        var request = new PaymentInstallmentMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.PaymentPlanId,
            command.InstallmentId);

        PaymentInstallmentMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ReversePaymentAsync(
                request,
                reason,
                state => DecideMutation(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            PaymentInstallmentMutationPersistenceResult.AccessDenied =>
                ReverseInstallmentPaymentResult.AccessDenied,

            PaymentInstallmentMutationPersistenceResult.NotFound =>
                ReverseInstallmentPaymentResult.NotFound,

            PaymentInstallmentMutationPersistenceResult.Succeeded =>
                ReverseInstallmentPaymentResult.Succeeded,

            _ => throw new InvalidOperationException(
                "Payment installment reversal returned an invalid result.")
        };
    }

    private PaymentInstallmentMutationDecision DecideMutation(
        PaymentInstallmentMutationPersistenceRequest request,
        PaymentInstallmentMutationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(
                FinanceAction.ReverseInstallmentPayment,
                actor.Role))
        {
            return PaymentInstallmentMutationDecision.AccessDenied;
        }

        if (state.Installment.OrganizationId != request.OrganizationId ||
            state.Installment.PaymentPlanId != request.PaymentPlanId ||
            state.Installment.Id != request.InstallmentId)
        {
            throw new InvalidOperationException(
                "Payment installment reversal received invalid locked state.");
        }

        if (state.Installment.PaidAt is not null)
        {
            state.Installment.ReversePayment();
        }

        return PaymentInstallmentMutationDecision.Persist;
    }
}
