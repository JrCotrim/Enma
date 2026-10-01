using Enma.Application.Authorization;
using Enma.Application.Time;

namespace Enma.Application.Finance.ClientSummary;

public sealed class GetClientFinanceSummaryUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly OperationalCalendar _operationalCalendar;

    public GetClientFinanceSummaryUseCase(
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

    public async Task<GetClientFinanceSummaryResult> ExecuteAsync(
        GetClientFinanceSummaryQuery query,
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
            return GetClientFinanceSummaryResult.AccessDenied;
        }

        DateOnly referenceDate = _operationalCalendar.GetToday();
        ClientFinanceSummaryReadModel? summary =
            await _readQueries.GetClientSummaryAsync(
                query.OrganizationId,
                query.ClientId,
                referenceDate,
                cancellationToken);

        return summary is null
            ? GetClientFinanceSummaryResult.NotFound
            : GetClientFinanceSummaryResult.Success(summary);
    }
}
