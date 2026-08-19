namespace Enma.Application.Documents.Inspection;

public sealed class LegalDocumentContentInspectionUnavailableException : Exception
{
    public LegalDocumentContentInspectionUnavailableException()
        : base("Document content inspection is temporarily unavailable.")
    {
    }
}
