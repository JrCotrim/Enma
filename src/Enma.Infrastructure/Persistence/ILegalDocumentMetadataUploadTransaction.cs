using Enma.Application.Documents.Upload;

namespace Enma.Infrastructure.Persistence;

public interface ILegalDocumentMetadataUploadTransaction
{
    Task<LegalDocumentUploadPersistenceResult> ExecuteAsync(
        LegalDocumentUploadPersistenceRequest request,
        Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
        LegalDocumentMetadataUploadAttempt attempt,
        CancellationToken cancellationToken = default);
}

public sealed class LegalDocumentMetadataUploadAttempt
{
    public bool CommitStarted { get; private set; }

    public void MarkCommitStarted()
    {
        CommitStarted = true;
    }
}
