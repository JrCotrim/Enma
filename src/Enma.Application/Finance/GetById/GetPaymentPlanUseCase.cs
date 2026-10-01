using Enma.Application.Authorization;
using Enma.Application.Time;

namespace Enma.Application.Finance.GetById;

public sealed class GetPaymentPlanUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly OperationalCalendar _operationalCalendar;

    public GetPaymentPlanUseCase(
        FinanceActionAuthorization actionAuthorization,
        IFinanceReadQueries readQueries,
        OperationalCalendar operationalCalendar)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);
        ArgumentNullException.ThrowIfNull(operationalCalendar);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
        _operationalCalendar = operationalCalendar;
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

        DateOnly referenceDate = _operationalCalendar.GetToday();
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
