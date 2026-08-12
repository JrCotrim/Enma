namespace Enma.Application.Clients.List;

public sealed class ListClientsResult
{
    private ListClientsResult(
        ListClientsResultStatus status,
        IReadOnlyList<ClientReadModel> items,
        int pageNumber,
        int pageSize)
    {
        Status = status;
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public ListClientsResultStatus Status { get; }

    public IReadOnlyList<ClientReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public static ListClientsResult AccessDenied { get; } = new(
        ListClientsResultStatus.AccessDenied,
        Array.Empty<ClientReadModel>(),
        0,
        0);

    public static ListClientsResult Success(
        IReadOnlyList<ClientReadModel> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ListClientsResult(
            ListClientsResultStatus.Succeeded,
            items.ToArray(),
            pageNumber,
            pageSize);
    }
}

public enum ListClientsResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
