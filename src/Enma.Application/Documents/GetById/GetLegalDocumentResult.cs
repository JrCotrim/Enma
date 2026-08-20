namespace Enma.Application.Documents.GetById;

public sealed class GetLegalDocumentResult
{
    private GetLegalDocumentResult(
        GetLegalDocumentResultStatus status,
        LegalDocumentMetadataReadModel? document)
    {
        Status = status;
        Document = document;
    }

    public GetLegalDocumentResultStatus Status { get; }

    public LegalDocumentMetadataReadModel? Document { get; }

    public static GetLegalDocumentResult AccessDenied { get; } = new(
        GetLegalDocumentResultStatus.AccessDenied,
        null);

    public static GetLegalDocumentResult NotFound { get; } = new(
        GetLegalDocumentResultStatus.NotFound,
        null);

    public static GetLegalDocumentResult InvalidInput { get; } = new(
        GetLegalDocumentResultStatus.InvalidInput,
        null);

    public static GetLegalDocumentResult Succeeded(
        LegalDocumentMetadataReadModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new GetLegalDocumentResult(
            GetLegalDocumentResultStatus.Succeeded,
            document);
    }
}

public enum GetLegalDocumentResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    Succeeded = 3
}
