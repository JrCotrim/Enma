namespace Enma.Application.Processes.List;

public sealed class ListLegalProcessesResult
{
    private ListLegalProcessesResult(
        ListLegalProcessesResultStatus status,
        IReadOnlyList<LegalProcessReadModel> items,
        int pageNumber,
        int pageSize)
    {
        Status = status;
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public ListLegalProcessesResultStatus Status { get; }

    public IReadOnlyList<LegalProcessReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public static ListLegalProcessesResult AccessDenied { get; } = new(
        ListLegalProcessesResultStatus.AccessDenied,
        Array.Empty<LegalProcessReadModel>(),
        0,
        0);

    public static ListLegalProcessesResult Success(
        IReadOnlyList<LegalProcessReadModel> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ListLegalProcessesResult(
            ListLegalProcessesResultStatus.Succeeded,
            items.ToArray(),
            pageNumber,
            pageSize);
    }
}

public enum ListLegalProcessesResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
