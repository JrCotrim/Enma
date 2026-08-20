namespace Enma.Application.Documents.Download;

public sealed class DownloadLegalDocumentResult
{
    private DownloadLegalDocumentResult(
        DownloadLegalDocumentResultStatus status,
        LegalDocumentDownload? download)
    {
        Status = status;
        Download = download;
    }

    public DownloadLegalDocumentResultStatus Status { get; }

    /// <summary>
    /// Gets the successful download resource. The caller owns and must dispose it.
    /// </summary>
    public LegalDocumentDownload? Download { get; }

    public static DownloadLegalDocumentResult AccessDenied { get; } = new(
        DownloadLegalDocumentResultStatus.AccessDenied,
        null);

    public static DownloadLegalDocumentResult NotFound { get; } = new(
        DownloadLegalDocumentResultStatus.NotFound,
        null);

    public static DownloadLegalDocumentResult InvalidInput { get; } = new(
        DownloadLegalDocumentResultStatus.InvalidInput,
        null);

    public static DownloadLegalDocumentResult ContentUnavailable { get; } = new(
        DownloadLegalDocumentResultStatus.ContentUnavailable,
        null);

    public static DownloadLegalDocumentResult Succeeded(
        LegalDocumentDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);

        return new DownloadLegalDocumentResult(
            DownloadLegalDocumentResultStatus.Succeeded,
            download);
    }
}

public enum DownloadLegalDocumentResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    ContentUnavailable = 3,
    Succeeded = 4
}
