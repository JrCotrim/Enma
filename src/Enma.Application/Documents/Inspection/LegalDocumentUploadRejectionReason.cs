namespace Enma.Application.Documents.Inspection;

public enum LegalDocumentUploadRejectionReason
{
    MissingFileName = 1,
    InvalidFileName = 2,
    FileNameTooLong = 3,
    UnsupportedFileType = 4,
    DangerousEmbeddedExtension = 5,
    MissingContentType = 6,
    ContentTypeMismatch = 7,
    EmptyFile = 8,
    FileTooLarge = 9,
    ContentLengthMismatch = 10,
    InvalidFileContent = 11
}
