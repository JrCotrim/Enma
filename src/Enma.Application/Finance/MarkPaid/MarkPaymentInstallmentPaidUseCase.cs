using Enma.Application.Authorization;

namespace Enma.Application.Finance.MarkPaid;

public sealed class MarkPaymentInstallmentPaidUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IPaymentInstallmentMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public MarkPaymentInstallmentPaidUseCase(
        FinanceActionAuthorization actionAuthorization,
        IPaymentInstallmentMutationPersistence mutationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<MarkPaymentInstallmentPaidResult> ExecuteAsync(
        MarkPaymentInstallmentPaidCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                command.UserId,
                command.OrganizationId,
                FinanceAction.MarkInstallmentPaid,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return MarkPaymentInstallmentPaidResult.AccessDenied;
        }

        if (command.PaymentPlanId == Guid.Empty ||
            command.InstallmentId == Guid.Empty)
        {
            return MarkPaymentInstallmentPaidResult.NotFound;
        }

        var request = new PaymentInstallmentMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.PaymentPlanId,
            command.InstallmentId);

        PaymentInstallmentMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                state => DecideMutation(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            PaymentInstallmentMutationPersistenceResult.AccessDenied =>
                MarkPaymentInstallmentPaidResult.AccessDenied,

            PaymentInstallmentMutationPersistenceResult.NotFound =>
                MarkPaymentInstallmentPaidResult.NotFound,

            PaymentInstallmentMutationPersistenceResult.Succeeded =>
                MarkPaymentInstallmentPaidResult.Succeeded,

            _ => throw new InvalidOperationException(
                "Payment installment mutation returned an invalid result.")
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
                FinanceAction.MarkInstallmentPaid,
                actor.Role))
        {
            return PaymentInstallmentMutationDecision.AccessDenied;
        }

        if (state.Installment.OrganizationId != request.OrganizationId ||
            state.Installment.PaymentPlanId != request.PaymentPlanId ||
            state.Installment.Id != request.InstallmentId)
        {
            throw new InvalidOperationException(
                "Payment installment mutation received invalid locked state.");
        }

        if (state.Installment.PaidAt is null)
        {
            state.Installment.MarkPaid(_timeProvider.GetUtcNow());
        }

        return PaymentInstallmentMutationDecision.Persist;
    }
}