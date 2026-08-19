namespace Enma.Application.Documents.Upload;

public interface ILegalDocumentUploadPersistence
{
    Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
        LegalDocumentUploadPersistenceRequest request,
        Stream content,
        Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
        CancellationToken cancellationToken = default);
}
