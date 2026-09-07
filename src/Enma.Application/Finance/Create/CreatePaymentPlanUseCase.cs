using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Finance;

namespace Enma.Application.Finance.Create;

public sealed class CreatePaymentPlanUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IPaymentPlanCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreatePaymentPlanUseCase(
        FinanceActionAuthorization actionAuthorization,
        IPaymentPlanCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreatePaymentPlanResult> ExecuteAsync(
        CreatePaymentPlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                command.UserId,
                command.OrganizationId,
                FinanceAction.CreatePaymentPlan,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CreatePaymentPlanResult.AccessDenied;
        }

        if (command.ClientId == Guid.Empty)
        {
            throw new RequestValidationException(
                FinanceErrors.ClientIdRequired);
        }

        var request = new PaymentPlanCreationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.ClientId);

        PaymentPlanCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                state => DecideCreation(command, request, state),
                cancellationToken);

        return persistenceResult.Status switch
        {
            PaymentPlanCreationDecisionStatus.AccessDenied =>
                CreatePaymentPlanResult.AccessDenied,
            PaymentPlanCreationDecisionStatus.RelatedClientUnavailable =>
                CreatePaymentPlanResult.RelatedClientUnavailable,
            PaymentPlanCreationDecisionStatus.Persist
                when persistenceResult.PaymentPlanId is Guid paymentPlanId =>
                CreatePaymentPlanResult.Succeeded(paymentPlanId),
            _ => throw new InvalidOperationException(
                "Payment plan creation persistence returned an invalid result.")
        };
    }

    private PaymentPlanCreationDecision DecideCreation(
        CreatePaymentPlanCommand command,
        PaymentPlanCreationPersistenceRequest request,
        PaymentPlanCreationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(
                FinanceAction.CreatePaymentPlan,
                actor.Role))
        {
            return PaymentPlanCreationDecision.AccessDenied;
        }

        if (!state.IsClientAvailable)
        {
            return PaymentPlanCreationDecision.RelatedClientUnavailable;
        }

        try
        {
            return PaymentPlanCreationDecision.Persist(
                new ClientPaymentPlan(
                    request.OrganizationId,
                    request.ClientId,
                    command.TotalAmount,
                    command.InstallmentCount,
                    command.FirstDueDate,
                    _timeProvider.GetUtcNow()));
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "amount" or
                "installmentCount" or
                "firstDueDate")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
