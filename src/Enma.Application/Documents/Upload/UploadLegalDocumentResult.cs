using Enma.Application.Documents.Inspection;

namespace Enma.Application.Documents.Upload;

public sealed class UploadLegalDocumentResult
{
    private UploadLegalDocumentResult(
        UploadLegalDocumentResultStatus status,
        Guid? documentId,
        LegalDocumentUploadRejectionReason? rejectionReason)
    {
        Status = status;
        DocumentId = documentId;
        RejectionReason = rejectionReason;
    }

    public UploadLegalDocumentResultStatus Status { get; }

    public Guid? DocumentId { get; }

    public LegalDocumentUploadRejectionReason? RejectionReason { get; }

    public static UploadLegalDocumentResult AccessDenied { get; } = new(
        UploadLegalDocumentResultStatus.AccessDenied,
        null,
        null);

    public static UploadLegalDocumentResult InvalidInput { get; } = new(
        UploadLegalDocumentResultStatus.InvalidInput,
        null,
        null);

    public static UploadLegalDocumentResult RelatedClientUnavailable { get; } = new(
        UploadLegalDocumentResultStatus.RelatedClientUnavailable,
        null,
        null);

    public static UploadLegalDocumentResult RelatedProcessUnavailable { get; } = new(
        UploadLegalDocumentResultStatus.RelatedProcessUnavailable,
        null,
        null);

    public static UploadLegalDocumentResult Rejected(
        LegalDocumentUploadRejectionReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new UploadLegalDocumentResult(
            UploadLegalDocumentResultStatus.Rejected,
            null,
            reason);
    }

    public static UploadLegalDocumentResult Succeeded(Guid documentId)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document id cannot be empty.",
                nameof(documentId));
        }

        return new UploadLegalDocumentResult(
            UploadLegalDocumentResultStatus.Succeeded,
            documentId,
            null);
    }
}

public enum UploadLegalDocumentResultStatus
{
    AccessDenied = 0,
    InvalidInput = 1,
    Rejected = 2,
    RelatedClientUnavailable = 3,
    RelatedProcessUnavailable = 4,
    Succeeded = 5
}
