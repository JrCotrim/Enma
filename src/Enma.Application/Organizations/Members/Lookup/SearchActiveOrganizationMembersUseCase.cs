using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Organizations.Members.Lookup;

public sealed class SearchActiveOrganizationMembersUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly IOrganizationMemberLookupQueries _queries;

    public SearchActiveOrganizationMembersUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        IOrganizationMemberLookupQueries queries)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(queries);

        _accessAuthorization = accessAuthorization;
        _queries = queries;
    }

    public async Task<SearchActiveOrganizationMembersResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string? search = null,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        string? normalizedSearch = NormalizeAndValidateSearch(search);
        ValidatePagination(pageNumber, pageSize);

        OrganizationAccessAuthorizationResult authorization =
            await _accessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (authorization.Status == OrganizationAccessAuthorizationStatus.Denied)
        {
            return SearchActiveOrganizationMembersResult.AccessDenied;
        }

        IReadOnlyList<OrganizationMemberLookupItem> members =
            await _queries.SearchAsync(
                organizationId,
                normalizedSearch,
                pageNumber,
                pageSize,
                cancellationToken);

        return SearchActiveOrganizationMembersResult.Success(
            members,
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
