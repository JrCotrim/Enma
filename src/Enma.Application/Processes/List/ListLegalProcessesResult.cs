namespace Enma.Application.Processes.List;

public sealed class ListLegalProcessesResult
{
    private ListLegalProcessesResult(
        ListLegalProcessesResultStatus status,
        IReadOnlyList<LegalProcessReadModel> items,
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

    public ListLegalProcessesResultStatus Status { get; }

    public IReadOnlyList<LegalProcessReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static ListLegalProcessesResult AccessDenied { get; } = new(
        ListLegalProcessesResultStatus.AccessDenied,
        Array.Empty<LegalProcessReadModel>(),
        0,
        0,
        false);

    public static ListLegalProcessesResult Success(
        IReadOnlyList<LegalProcessReadModel> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        bool hasNext = items.Count > pageSize;

        return new ListLegalProcessesResult(
            ListLegalProcessesResultStatus.Succeeded,
            items.Take(pageSize).ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum ListLegalProcessesResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
