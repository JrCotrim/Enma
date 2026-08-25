using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Organizations.Members.List;

public sealed class ListOrganizationMembersUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IOrganizationMemberAdministrationQueries _queries;

    public ListOrganizationMembersUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationMemberAdministrationQueries queries)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(queries);
        _authorization = authorization;
        _queries = queries;
    }

    public async Task<ListOrganizationMembersResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string? status = null,
        string? search = null,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        OrganizationMembershipStatus membershipStatus = ParseStatus(status);
        string? normalizedSearch = NormalizeAndValidateSearch(search);
        ValidatePagination(pageNumber, pageSize);

        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (!authorization.Allows(OrganizationAdministrationAction.ViewTeam))
        {
            return ListOrganizationMembersResult.AccessDenied;
        }

        bool includeAdministrativeDetails = authorization.Allows(
            OrganizationAdministrationAction.ViewTeamAdministrationDetails);

        if (!includeAdministrativeDetails &&
            membershipStatus == OrganizationMembershipStatus.Inactive)
        {
            return ListOrganizationMembersResult.AccessDenied;
        }

        var query = new OrganizationMemberAdministrationQuery(
            organizationId,
            membershipStatus,
            normalizedSearch,
            pageNumber,
            pageSize,
            includeAdministrativeDetails
                ? OrganizationMemberDetailLevel.Administrative
                : OrganizationMemberDetailLevel.Basic);
        OrganizationMemberAdministrationPage page = await _queries.ListAsync(
            query,
            cancellationToken);

        return ListOrganizationMembersResult.Success(page, pageNumber, pageSize);
    }

    private static OrganizationMembershipStatus ParseStatus(string? status)
    {
        string normalizedStatus = status?.Trim() ?? "active";

        return normalizedStatus switch
        {
            "active" => OrganizationMembershipStatus.Active,
            "inactive" => OrganizationMembershipStatus.Inactive,
            _ => throw new RequestValidationException(
                "Status must be either 'active' or 'inactive'.")
        };
    }

    private static string? NormalizeAndValidateSearch(string? search)
    {
        string? normalizedSearch = search?.Trim();

        if (normalizedSearch?.Length > MaximumSearchLength)
        {
            throw new RequestValidationException(
                $"Search must not exceed {MaximumSearchLength} characters.");
        }

        return string.IsNullOrEmpty(normalizedSearch) ? null : normalizedSearch;
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
