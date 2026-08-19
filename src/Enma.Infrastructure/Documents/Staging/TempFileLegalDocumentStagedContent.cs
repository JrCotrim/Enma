using Enma.Application.Documents.Staging;

namespace Enma.Infrastructure.Documents.Staging;

internal sealed class TempFileLegalDocumentStagedContent
    : ILegalDocumentStagedContent
{
    private FileStream? content;
    private readonly byte[] contentHashSha256;

    public TempFileLegalDocumentStagedContent(
        FileStream content,
        long contentLength,
        byte[] contentHashSha256)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentHashSha256);

        this.content = content;
        ContentLength = contentLength;
        this.contentHashSha256 = contentHashSha256;
    }

    public Stream Content => GetActiveContent();

    public long ContentLength { get; }

    public ReadOnlyMemory<byte> ContentHashSha256
    {
        get
        {
            _ = GetActiveContent();
            return contentHashSha256;
        }
    }

    public async ValueTask DisposeAsync()
    {
        FileStream? currentContent = Interlocked.Exchange(
            ref content,
            null);

        if (currentContent is not null)
        {
            await currentContent.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private FileStream GetActiveContent()
    {
        return content
            ?? throw new ObjectDisposedException(
                nameof(TempFileLegalDocumentStagedContent));
    }
}
