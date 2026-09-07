using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Finance.List;

public sealed class ListPaymentPlansUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly FinanceActionAuthorization _actionAuthorization;
    private readonly IFinanceReadQueries _readQueries;
    private readonly TimeProvider _timeProvider;

    public ListPaymentPlansUseCase(
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

    public async Task<ListPaymentPlansResult> ExecuteAsync(
        ListPaymentPlansQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Validate(query);

        FinanceActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                FinanceAction.View,
                cancellationToken);

        if (authorization == FinanceActionAuthorizationResult.Denied)
        {
            return ListPaymentPlansResult.AccessDenied;
        }

        DateOnly referenceDate = DateOnly.FromDateTime(
            _timeProvider.GetUtcNow().UtcDateTime);
        IReadOnlyList<PaymentPlanListItemReadModel> items =
            await _readQueries.ListAsync(
                query.OrganizationId,
                query.ClientId,
                referenceDate,
                query.PageNumber,
                query.PageSize,
                cancellationToken);

        return ListPaymentPlansResult.Success(
            items,
            query.PageNumber,
            query.PageSize);
    }

    private static void Validate(ListPaymentPlansQuery query)
    {
        if (query.ClientId == Guid.Empty)
        {
            throw new RequestValidationException(
                "Client id cannot be empty.");
        }

        if (query.PageNumber < 1)
        {
            throw new RequestValidationException(
                "Page number must be at least 1.");
        }

        if (query.PageSize < 1 || query.PageSize > MaximumPageSize)
        {
            throw new RequestValidationException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        long skippedItems = ((long)query.PageNumber - 1) * query.PageSize;
        if (skippedItems > int.MaxValue)
        {
            throw new RequestValidationException(
                "The requested page offset is too large.");
        }
    }
}
