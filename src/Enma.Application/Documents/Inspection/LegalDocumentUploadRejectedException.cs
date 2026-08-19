namespace Enma.Application.Documents.Inspection;

public sealed class LegalDocumentUploadRejectedException : Exception
{
    public LegalDocumentUploadRejectedException(
        LegalDocumentUploadRejectionReason reason)
        : base(GetSafeMessage(reason))
    {
        Reason = reason;
    }

    public LegalDocumentUploadRejectionReason Reason { get; }

    private static string GetSafeMessage(
        LegalDocumentUploadRejectionReason reason)
    {
        return reason switch
        {
            LegalDocumentUploadRejectionReason.MissingFileName =>
                "The document file name is required.",
            LegalDocumentUploadRejectionReason.InvalidFileName =>
                "The document file name is invalid.",
            LegalDocumentUploadRejectionReason.FileNameTooLong =>
                "The document file name exceeds the supported length.",
            LegalDocumentUploadRejectionReason.UnsupportedFileType =>
                "The document file type is not supported.",
            LegalDocumentUploadRejectionReason.DangerousEmbeddedExtension =>
                "The document file name contains a disallowed embedded file type.",
            LegalDocumentUploadRejectionReason.MissingContentType =>
                "The document content type is required.",
            LegalDocumentUploadRejectionReason.ContentTypeMismatch =>
                "The document content type does not match the file extension.",
            LegalDocumentUploadRejectionReason.EmptyFile =>
                "The document file must not be empty.",
            LegalDocumentUploadRejectionReason.FileTooLarge =>
                "The document file exceeds the maximum supported size.",
            LegalDocumentUploadRejectionReason.ContentLengthMismatch =>
                "The document content length does not match the declared size.",
            LegalDocumentUploadRejectionReason.InvalidFileContent =>
                "The document file content is invalid for the declared file type.",
            _ =>
                "The document upload was rejected."
        };
    }
}
