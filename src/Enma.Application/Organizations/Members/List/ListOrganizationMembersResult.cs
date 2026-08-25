namespace Enma.Application.Organizations.Members.List;

public sealed class ListOrganizationMembersResult
{
    private ListOrganizationMembersResult(
        ListOrganizationMembersResultStatus status,
        IReadOnlyList<OrganizationMemberAdministrationReadModel> items,
        int pageNumber,
        int pageSize,
        int totalCount)
    {
        Status = status;
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    public ListOrganizationMembersResultStatus Status { get; }

    public IReadOnlyList<OrganizationMemberAdministrationReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public int TotalCount { get; }

    public static ListOrganizationMembersResult AccessDenied { get; } = new(
        ListOrganizationMembersResultStatus.AccessDenied,
        Array.Empty<OrganizationMemberAdministrationReadModel>(),
        0,
        0,
        0);

    public static ListOrganizationMembersResult Success(
        OrganizationMemberAdministrationPage page,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.TotalCount < 0 || page.Items.Count > pageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        return new ListOrganizationMembersResult(
            ListOrganizationMembersResultStatus.Succeeded,
            page.Items.ToArray(),
            pageNumber,
            pageSize,
            page.TotalCount);
    }
}

public enum ListOrganizationMembersResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
