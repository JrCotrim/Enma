using Enma.Domain.Documents;

namespace Enma.Application.Documents.Upload;

public sealed class LegalDocumentUploadDecision
{
    private LegalDocumentUploadDecision(
        LegalDocumentUploadDecisionStatus status,
        LegalDocument? legalDocument)
    {
        Status = status;
        LegalDocument = legalDocument;
    }

    public LegalDocumentUploadDecisionStatus Status { get; }

    public LegalDocument? LegalDocument { get; }

    public static LegalDocumentUploadDecision AccessDenied { get; } = new(
        LegalDocumentUploadDecisionStatus.AccessDenied,
        null);

    public static LegalDocumentUploadDecision RelatedClientUnavailable { get; } = new(
        LegalDocumentUploadDecisionStatus.RelatedClientUnavailable,
        null);

    public static LegalDocumentUploadDecision RelatedProcessUnavailable { get; } = new(
        LegalDocumentUploadDecisionStatus.RelatedProcessUnavailable,
        null);

    public static LegalDocumentUploadDecision Persist(LegalDocument legalDocument)
    {
        ArgumentNullException.ThrowIfNull(legalDocument);

        return new LegalDocumentUploadDecision(
            LegalDocumentUploadDecisionStatus.Persist,
            legalDocument);
    }
}

public enum LegalDocumentUploadDecisionStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    RelatedProcessUnavailable = 2,
    Persist = 3
}
