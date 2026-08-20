using Enma.Application.Authorization;
using Enma.Application.Documents.Storage;

namespace Enma.Application.Documents.Download;

public sealed class DownloadLegalDocumentUseCase
{
    private readonly LegalDocumentReadAuthorization readAuthorization;
    private readonly ILegalDocumentContentReadQueries contentReadQueries;
    private readonly ILegalDocumentStorage storage;

    public DownloadLegalDocumentUseCase(
        LegalDocumentReadAuthorization readAuthorization,
        ILegalDocumentContentReadQueries contentReadQueries,
        ILegalDocumentStorage storage)
    {
        ArgumentNullException.ThrowIfNull(readAuthorization);
        ArgumentNullException.ThrowIfNull(contentReadQueries);
        ArgumentNullException.ThrowIfNull(storage);

        this.readAuthorization = readAuthorization;
        this.contentReadQueries = contentReadQueries;
        this.storage = storage;
    }

    public async Task<DownloadLegalDocumentResult> ExecuteAsync(
        DownloadLegalDocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LegalDocumentReadAuthorizationResult authorization =
            await readAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                LegalDocumentReadAction.DownloadContent,
                cancellationToken);

        if (authorization == LegalDocumentReadAuthorizationResult.Denied)
        {
            return DownloadLegalDocumentResult.AccessDenied;
        }

        if (query.DocumentId == Guid.Empty)
        {
            return DownloadLegalDocumentResult.InvalidInput;
        }

        LegalDocumentContentReadModel? document =
            await contentReadQueries.FindAsync(
                query.OrganizationId,
                query.DocumentId,
                cancellationToken);

        if (document is null)
        {
            return DownloadLegalDocumentResult.NotFound;
        }

        if (!LegalDocumentStorageObjectKey.TryParse(
                document.StoredObjectKey,
                out LegalDocumentStorageObjectKey? objectKey) ||
            objectKey is null)
        {
            return DownloadLegalDocumentResult.ContentUnavailable;
        }

        ILegalDocumentStorageReadHandle storageReadHandle;

        try
        {
            storageReadHandle = await storage.OpenReadAsync(
                objectKey,
                cancellationToken);
        }
        catch (LegalDocumentStorageException)
        {
            return DownloadLegalDocumentResult.ContentUnavailable;
        }

        try
        {
            if (storageReadHandle.ContentLength != document.SizeBytes)
            {
                await storageReadHandle.DisposeAsync();
                return DownloadLegalDocumentResult.ContentUnavailable;
            }

            var download = new LegalDocumentDownload(
                document.DocumentId,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                storageReadHandle);

            return DownloadLegalDocumentResult.Succeeded(download);
        }
        catch
        {
            await storageReadHandle.DisposeAsync();
            throw;
        }
    }
}
