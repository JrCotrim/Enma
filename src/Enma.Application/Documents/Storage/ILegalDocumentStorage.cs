namespace Enma.Application.Documents.Storage;

public interface ILegalDocumentStorage
{
    Task StoreAsync(
        LegalDocumentStorageObjectKey objectKey,
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
