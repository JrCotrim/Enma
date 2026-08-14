namespace Enma.Application.Tasks.List;

public sealed class ListLegalTasksResult
{
    private ListLegalTasksResult(
        ListLegalTasksResultStatus status,
        IReadOnlyList<LegalTaskListItem> items,
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

    public ListLegalTasksResultStatus Status { get; }

    public IReadOnlyList<LegalTaskListItem> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static ListLegalTasksResult AccessDenied { get; } = new(
        ListLegalTasksResultStatus.AccessDenied,
        Array.Empty<LegalTaskListItem>(),
        0,
        0,
        false);

    public static ListLegalTasksResult InvalidInput { get; } = new(
        ListLegalTasksResultStatus.InvalidInput,
        Array.Empty<LegalTaskListItem>(),
        0,
        0,
        false);

    public static ListLegalTasksResult Succeeded(
        IReadOnlyList<LegalTaskListItem> items,
        int pageNumber,
        int pageSize,
        bool hasNext)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ListLegalTasksResult(
            ListLegalTasksResultStatus.Succeeded,
            items.ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum ListLegalTasksResultStatus
{
    AccessDenied = 0,
    InvalidInput = 1,
    Succeeded = 2
}
