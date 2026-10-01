using Enma.Application.Authorization;
using Enma.Application.Time;

namespace Enma.Application.Finance.Overview;

public sealed class GetFinanceOverviewUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly OperationalCalendar _operationalCalendar;

    public GetFinanceOverviewUseCase(
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

    public async Task<GetFinanceOverviewResult> ExecuteAsync(
        GetFinanceOverviewQuery query,
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
            return GetFinanceOverviewResult.AccessDenied;
        }

        DateOnly referenceDate = _operationalCalendar.GetToday();
        FinanceOverviewReadModel overview =
            await _readQueries.GetOverviewAsync(
                query.OrganizationId,
                referenceDate,
                cancellationToken);

        return GetFinanceOverviewResult.Success(overview);
    }
}
