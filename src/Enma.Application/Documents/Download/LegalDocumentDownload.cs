using Enma.Application.Documents.Storage;

namespace Enma.Application.Documents.Download;

/// <summary>
/// Owns a private legal-document content stream.
/// </summary>
/// <remarks>
/// The caller must dispose this resource after consuming <see cref="Content"/>.
/// Disposal releases the content stream and all underlying storage resources.
/// </remarks>
public sealed class LegalDocumentDownload : IAsyncDisposable
{
    private ILegalDocumentStorageReadHandle? storageReadHandle;

    internal LegalDocumentDownload(
        Guid documentId,
        string originalFileName,
        string contentType,
        long sizeBytes,
        ILegalDocumentStorageReadHandle storageReadHandle)
    {
        ArgumentNullException.ThrowIfNull(storageReadHandle);

        DocumentId = documentId;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        this.storageReadHandle = storageReadHandle;
    }

    public Guid DocumentId { get; }

    public string OriginalFileName { get; }

    public string ContentType { get; }

    public long SizeBytes { get; }

    public Stream Content => GetActiveStorageReadHandle().Content;

    public ValueTask DisposeAsync()
    {
        ILegalDocumentStorageReadHandle? current = Interlocked.Exchange(
            ref storageReadHandle,
            null);

        GC.SuppressFinalize(this);

        return current is null
            ? ValueTask.CompletedTask
            : current.DisposeAsync();
    }

    private ILegalDocumentStorageReadHandle GetActiveStorageReadHandle()
    {
        return Volatile.Read(ref storageReadHandle)
            ?? throw new ObjectDisposedException(
                nameof(LegalDocumentDownload));
    }
}
