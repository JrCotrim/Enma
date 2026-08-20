namespace Enma.Application.Documents.Download;

public interface ILegalDocumentContentReadQueries
{
    Task<LegalDocumentContentReadModel?> FindAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default);
}

public sealed record LegalDocumentContentReadModel(
    Guid DocumentId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string StoredObjectKey);
