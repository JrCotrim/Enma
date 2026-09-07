using Enma.Application.Authorization;

namespace Enma.Application.Finance.Overview;

public sealed class GetFinanceOverviewUseCase
{
    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly TimeProvider _timeProvider;

    public GetFinanceOverviewUseCase(
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

        DateOnly referenceDate = DateOnly.FromDateTime(
            _timeProvider.GetUtcNow().UtcDateTime);
        FinanceOverviewReadModel overview =
            await _readQueries.GetOverviewAsync(
                query.OrganizationId,
                referenceDate,
                cancellationToken);

        return GetFinanceOverviewResult.Success(overview);
    }
}
