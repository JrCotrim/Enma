using Enma.Application.Authorization;

namespace Enma.Application.Finance.GetById;

public sealed class GetPaymentPlanUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly TimeProvider _timeProvider;

    public GetPaymentPlanUseCase(
        FinanceActionAuthorization actionAuthorization,
        IFinanceReadQueries readQueries,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
        _timeProvider = timeProvider;
    }

    public async Task<GetPaymentPlanResult> ExecuteAsync(
        GetPaymentPlanQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        FinanceActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                FinanceAction.View,
                cancellationToken);

        if (authorization == FinanceActionAuthorizationResult.Denied)
        {
            return GetPaymentPlanResult.AccessDenied;
        }

        DateOnly referenceDate = DateOnly.FromDateTime(
            _timeProvider.GetUtcNow().UtcDateTime);
        PaymentPlanDetailReadModel? paymentPlan =
            await _readQueries.FindAsync(
                query.OrganizationId,
                query.PaymentPlanId,
                referenceDate,
                cancellationToken);

        return paymentPlan is null
            ? GetPaymentPlanResult.NotFound
            : GetPaymentPlanResult.Success(paymentPlan);
    }
}
