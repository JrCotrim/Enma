namespace Enma.Application.Documents.Inspection;

public sealed record LegalDocumentUploadAdmission(
    string OriginalFileName,
    string Extension,
    string CanonicalContentType,
    LegalDocumentFileType FileType,
    long ContentLength);
