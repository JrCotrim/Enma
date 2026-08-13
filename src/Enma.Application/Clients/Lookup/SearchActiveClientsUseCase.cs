using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Clients.Lookup;

public sealed class SearchActiveClientsUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IActiveClientLookupQueries _queries;

    public SearchActiveClientsUseCase(
        ClientActionAuthorization actionAuthorization,
        IActiveClientLookupQueries queries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(queries);

        _actionAuthorization = actionAuthorization;
        _queries = queries;
    }

    public async Task<SearchActiveClientsResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string? search = null,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        string? normalizedSearch = NormalizeAndValidateSearch(search);
        ValidatePagination(pageNumber, pageSize);

        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.View,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return SearchActiveClientsResult.AccessDenied;
        }

        IReadOnlyList<ActiveClientLookupItem> clients =
            await _queries.SearchAsync(
                organizationId,
                normalizedSearch,
                pageNumber,
                pageSize,
                cancellationToken);

        return SearchActiveClientsResult.Success(
            clients,
            pageNumber,
            pageSize);
    }

    private static string? NormalizeAndValidateSearch(string? search)
    {
        if (search?.Length > MaximumSearchLength)
        {
            throw new RequestValidationException(
                $"Search must not exceed {MaximumSearchLength} characters.");
        }

        string? normalizedSearch = search?.Trim();

        return string.IsNullOrEmpty(normalizedSearch)
            ? null
            : normalizedSearch;
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
                "The requested page offset is too large.");
        }
    }
}
