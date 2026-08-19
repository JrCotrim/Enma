namespace Enma.Application.Documents.Upload;

public sealed class LegalDocumentUploadPersistenceResult
{
    private LegalDocumentUploadPersistenceResult(
        LegalDocumentUploadPersistenceResultStatus status,
        Guid? documentId)
    {
        Status = status;
        DocumentId = documentId;
    }

    public LegalDocumentUploadPersistenceResultStatus Status { get; }

    public Guid? DocumentId { get; }

    public static LegalDocumentUploadPersistenceResult AccessDenied { get; } = new(
        LegalDocumentUploadPersistenceResultStatus.AccessDenied,
        null);

    public static LegalDocumentUploadPersistenceResult RelatedClientUnavailable { get; } = new(
        LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable,
        null);

    public static LegalDocumentUploadPersistenceResult RelatedProcessUnavailable { get; } = new(
        LegalDocumentUploadPersistenceResultStatus.RelatedProcessUnavailable,
        null);

    public static LegalDocumentUploadPersistenceResult Persisted(Guid documentId)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document id cannot be empty.",
                nameof(documentId));
        }

        return new LegalDocumentUploadPersistenceResult(
            LegalDocumentUploadPersistenceResultStatus.Persisted,
            documentId);
    }
}

public enum LegalDocumentUploadPersistenceResultStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    RelatedProcessUnavailable = 2,
    Persisted = 3
}
