using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Processes.Lookup;

public sealed class SearchLegalProcessesUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessLookupQueries _queries;

    public SearchLegalProcessesUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessLookupQueries queries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(queries);

        _actionAuthorization = actionAuthorization;
        _queries = queries;
    }

    public async Task<SearchLegalProcessesResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string? search = null,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        string? normalizedSearch = NormalizeAndValidateSearch(search);
        ValidatePagination(pageNumber, pageSize);

        ProcessActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ProcessAction.View,
                cancellationToken);

        if (authorization == ProcessActionAuthorizationResult.Denied)
        {
            return SearchLegalProcessesResult.AccessDenied;
        }

        IReadOnlyList<LegalProcessLookupItem> legalProcesses =
            await _queries.SearchAsync(
                organizationId,
                normalizedSearch,
                pageNumber,
                pageSize,
                cancellationToken);

        return SearchLegalProcessesResult.Success(
            legalProcesses,
            pageNumber,
            pageSize);
    }

    private static string? NormalizeAndValidateSearch(string? search)
    {
        string? normalizedSearch = search?.Trim();

        if (normalizedSearch?.Length > MaximumSearchLength)
        {
            throw new RequestValidationException(
                $"Search must not exceed {MaximumSearchLength} characters.");
        }

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
