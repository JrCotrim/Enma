namespace Enma.Application.Documents.Upload;

public sealed class LegalDocumentUploadOutcomeUnknownException : Exception
{
    public LegalDocumentUploadOutcomeUnknownException()
        : base(
            "The legal document upload outcome could not be confirmed. Do not automatically retry this upload.")
    {
    }
}

public sealed class LegalDocumentUploadCompensationUnavailableException : Exception
{
    public LegalDocumentUploadCompensationUnavailableException()
        : base(
            "The legal document upload could not be safely finalized because cleanup is temporarily unavailable.")
    {
    }
}
