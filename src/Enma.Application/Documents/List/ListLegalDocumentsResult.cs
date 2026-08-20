namespace Enma.Application.Documents.List;

public sealed class ListLegalDocumentsResult
{
    private ListLegalDocumentsResult(
        ListLegalDocumentsResultStatus status,
        IReadOnlyList<LegalDocumentMetadataReadModel> items,
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

    public ListLegalDocumentsResultStatus Status { get; }

    public IReadOnlyList<LegalDocumentMetadataReadModel> Items { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public bool HasNext { get; }

    public static ListLegalDocumentsResult AccessDenied { get; } = new(
        ListLegalDocumentsResultStatus.AccessDenied,
        Array.Empty<LegalDocumentMetadataReadModel>(),
        0,
        0,
        false);

    public static ListLegalDocumentsResult InvalidInput { get; } = new(
        ListLegalDocumentsResultStatus.InvalidInput,
        Array.Empty<LegalDocumentMetadataReadModel>(),
        0,
        0,
        false);

    public static ListLegalDocumentsResult Succeeded(
        IReadOnlyList<LegalDocumentMetadataReadModel> items,
        int pageNumber,
        int pageSize,
        bool hasNext)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ListLegalDocumentsResult(
            ListLegalDocumentsResultStatus.Succeeded,
            items.ToArray(),
            pageNumber,
            pageSize,
            hasNext);
    }
}

public enum ListLegalDocumentsResultStatus
{
    AccessDenied = 0,
    InvalidInput = 1,
    Succeeded = 2
}
