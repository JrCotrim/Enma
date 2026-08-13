namespace Enma.Application.Deadlines.List;

public sealed class ListLegalDeadlinesResult
{
    private ListLegalDeadlinesResult(
        ListLegalDeadlinesResultStatus status,
        IReadOnlyList<LegalDeadlineListItem> items,
        int pageNumber,
        int pageSize)
    {
        Status = status;
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public ListLegalDeadlinesResultStatus Status { get; }

    public IReadOnlyList<LegalDeadlineListItem> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public static ListLegalDeadlinesResult AccessDenied { get; } = new(
        ListLegalDeadlinesResultStatus.AccessDenied,
        Array.Empty<LegalDeadlineListItem>(),
        0,
        0);

    public static ListLegalDeadlinesResult Success(
        IReadOnlyList<LegalDeadlineListItem> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ListLegalDeadlinesResult(
            ListLegalDeadlinesResultStatus.Succeeded,
            items.ToArray(),
            pageNumber,
            pageSize);
    }
}

public enum ListLegalDeadlinesResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
