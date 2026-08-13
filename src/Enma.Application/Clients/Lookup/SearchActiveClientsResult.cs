namespace Enma.Application.Clients.Lookup;

public sealed class SearchActiveClientsResult
{
    private SearchActiveClientsResult(
        SearchActiveClientsResultStatus status,
        IReadOnlyList<ActiveClientLookupItem> items,
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

    public SearchActiveClientsResultStatus Status { get; }

    public IReadOnlyList<ActiveClientLookupItem> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static SearchActiveClientsResult AccessDenied { get; } = new(
        SearchActiveClientsResultStatus.AccessDenied,
        Array.Empty<ActiveClientLookupItem>(),
        0,
        0,
        false);

    public static SearchActiveClientsResult Success(
        IReadOnlyList<ActiveClientLookupItem> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        bool hasNext = items.Count > pageSize;

        return new SearchActiveClientsResult(
            SearchActiveClientsResultStatus.Succeeded,
            items.Take(pageSize).ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum SearchActiveClientsResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
