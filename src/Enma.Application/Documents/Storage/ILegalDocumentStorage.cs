namespace Enma.Application.Documents.Storage;

public interface ILegalDocumentStorage
{
    Task<LegalDocumentStorageObjectKey> StoreAsync(
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
        LegalDocumentStorageObjectKey objectKey,
        CancellationToken cancellationToken = default);

    Task DeleteIfExistsAsync(
        LegalDocumentStorageObjectKey objectKey,
        CancellationToken cancellationToken = default);
}