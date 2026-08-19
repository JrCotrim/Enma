namespace Enma.Application.Documents.Staging;

public sealed class LegalDocumentContentStagingUnavailableException : Exception
{
    public LegalDocumentContentStagingUnavailableException()
        : base("Document content staging is temporarily unavailable.")
    {
    }
}
