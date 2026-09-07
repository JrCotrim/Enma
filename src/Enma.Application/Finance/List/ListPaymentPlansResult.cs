namespace Enma.Application.Finance.List;

public sealed class ListPaymentPlansResult
{
    private ListPaymentPlansResult(
        ListPaymentPlansResultStatus status,
        IReadOnlyList<PaymentPlanListItemReadModel> items,
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

    public ListPaymentPlansResultStatus Status { get; }
    public IReadOnlyList<PaymentPlanListItemReadModel> Items { get; }
    public int PageNumber { get; }
    public int PageSize { get; }
    public bool HasNext { get; }

    public static ListPaymentPlansResult AccessDenied { get; } = new(
        ListPaymentPlansResultStatus.AccessDenied,
        Array.Empty<PaymentPlanListItemReadModel>(),
        0,
        0,
        false);

    public static ListPaymentPlansResult Success(
        IReadOnlyList<PaymentPlanListItemReadModel> items,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        bool hasNext = items.Count > pageSize;

        return new ListPaymentPlansResult(
            ListPaymentPlansResultStatus.Succeeded,
            items.Take(pageSize).ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum ListPaymentPlansResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
