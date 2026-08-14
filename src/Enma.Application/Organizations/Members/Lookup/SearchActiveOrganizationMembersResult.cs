namespace Enma.Application.Organizations.Members.Lookup;

public sealed class SearchActiveOrganizationMembersResult
{
    private SearchActiveOrganizationMembersResult(
        SearchActiveOrganizationMembersResultStatus status,
        IReadOnlyList<OrganizationMemberLookupItem> items,
        int pageNumber,
        int pageSize,
        bool hasNext)
    {
        Status = status;
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        HasNext = hasNext;
    }

    public SearchActiveOrganizationMembersResultStatus Status { get; }

    public IReadOnlyList<OrganizationMemberLookupItem> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static SearchActiveOrganizationMembersResult AccessDenied { get; } = new(
        SearchActiveOrganizationMembersResultStatus.AccessDenied,
        Array.Empty<OrganizationMemberLookupItem>(),
        0,
        0,
        false);

    public static SearchActiveOrganizationMembersResult Success(
        IReadOnlyList<OrganizationMemberLookupItem> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        bool hasNext = items.Count > pageSize;

        return new SearchActiveOrganizationMembersResult(
            SearchActiveOrganizationMembersResultStatus.Succeeded,
            items.Take(pageSize).ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum SearchActiveOrganizationMembersResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
