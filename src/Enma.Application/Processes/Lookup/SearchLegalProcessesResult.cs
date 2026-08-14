namespace Enma.Application.Processes.Lookup;

public sealed class SearchLegalProcessesResult
{
    private SearchLegalProcessesResult(
        SearchLegalProcessesResultStatus status,
        IReadOnlyList<LegalProcessLookupItem> items,
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

    public SearchLegalProcessesResultStatus Status { get; }

    public IReadOnlyList<LegalProcessLookupItem> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static SearchLegalProcessesResult AccessDenied { get; } = new(
        SearchLegalProcessesResultStatus.AccessDenied,
        Array.Empty<LegalProcessLookupItem>(),
        0,
        0,
        false);

    public static SearchLegalProcessesResult Success(
        IReadOnlyList<LegalProcessLookupItem> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        bool hasNext = items.Count > pageSize;

        return new SearchLegalProcessesResult(
            SearchLegalProcessesResultStatus.Succeeded,
            items.Take(pageSize).ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum SearchLegalProcessesResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
