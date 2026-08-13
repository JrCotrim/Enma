using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Deadlines.List;

public sealed class ListLegalDeadlinesUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineReadQueries _readQueries;

    public ListLegalDeadlinesUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalDeadlinesResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePagination(pageNumber, pageSize);

        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.View,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
        {
            return ListLegalDeadlinesResult.AccessDenied;
        }

        IReadOnlyList<LegalDeadlineListItem> legalDeadlines =
            await _readQueries.ListAsync(
                organizationId,
                pageNumber,
                pageSize,
                cancellationToken);

        return ListLegalDeadlinesResult.Success(
            legalDeadlines,
            pageNumber,
            pageSize);
    }

    private static void ValidatePagination(int pageNumber, int pageSize)
    {
        if (pageNumber < 1)
        {
            throw new RequestValidationException(
                "Page number must be at least 1.");
        }

        if (pageSize < 1 || pageSize > MaximumPageSize)
        {
            throw new RequestValidationException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        long skippedItems = ((long)pageNumber - 1) * pageSize;

        if (skippedItems > int.MaxValue)
        {
            throw new RequestValidationException(
                "Pagination offset is too large.");
        }
    }
}
