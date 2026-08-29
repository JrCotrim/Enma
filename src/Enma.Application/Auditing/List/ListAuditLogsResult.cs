namespace Enma.Application.Auditing.List;

public sealed class ListAuditLogsResult
{
    private ListAuditLogsResult(
        ListAuditLogsResultStatus status,
        IReadOnlyList<AuditLogReadModel> items,
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

    public ListAuditLogsResultStatus Status { get; }

    public IReadOnlyList<AuditLogReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public int TotalCount { get; }

    public static ListAuditLogsResult AccessDenied { get; } = new(
        ListAuditLogsResultStatus.AccessDenied,
        Array.Empty<AuditLogReadModel>(),
        0,
        0,
        0);

    public static ListAuditLogsResult Success(
        AuditLogReadPage page,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.TotalCount < 0 || page.Items.Count > pageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        return new ListAuditLogsResult(
            ListAuditLogsResultStatus.Succeeded,
            page.Items.ToArray(),
            pageNumber,
            pageSize,
            page.TotalCount);
    }
}

public enum ListAuditLogsResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
