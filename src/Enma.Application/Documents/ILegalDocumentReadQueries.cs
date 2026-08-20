namespace Enma.Application.Documents;

public interface ILegalDocumentReadQueries
{
    Task<LegalDocumentMetadataReadModel?> FindAsync(
        Guid documentId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<LegalDocumentListReadPage> ListAsync(
        LegalDocumentListReadRequest request,
        CancellationToken cancellationToken = default);
}
