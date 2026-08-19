namespace Enma.Domain.Documents;

public static class LegalDocumentErrors
{
    public const string OrganizationIdRequired =
        "Document organization is required.";
    public const string ClientIdInvalid =
        "Document client identifier must be valid when provided.";
    public const string ProcessIdInvalid =
        "Document process identifier must be valid when provided.";
    public const string ClassificationInvalid =
        "Document cannot reference a client and a process at the same time.";
    public const string OriginalFileNameRequired =
        "Document original file name is required.";
    public const string OriginalFileNameInvalid =
        "Document original file name is invalid.";
    public const string OriginalFileNameTooLong =
        "Document original file name exceeds the supported length.";
    public const string StoredObjectKeyInvalid =
        "Document storage object key is invalid.";
    public const string ContentTypeInvalid =
        "Document content type is invalid.";
    public const string SizeBytesInvalid =
        "Document size must be within the supported range.";
    public const string ContentHashRequired =
        "Document content hash is required.";
    public const string ContentHashLengthInvalid =
        "Document content hash must contain exactly 32 bytes.";
    public const string UploadedByMembershipIdRequired =
        "Document uploader membership is required.";
    public const string CreatedAtInvalid =
        "Document creation date must be a valid value.";
}
