using Enma.Application.Authorization;

namespace Enma.Application.Finance.ClientSummary;

public sealed class GetClientFinanceSummaryUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly TimeProvider _timeProvider;

    public GetClientFinanceSummaryUseCase(
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

        DateOnly referenceDate = DateOnly.FromDateTime(
            _timeProvider.GetUtcNow().UtcDateTime);
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
