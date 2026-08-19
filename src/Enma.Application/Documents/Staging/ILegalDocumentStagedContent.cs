namespace Enma.Application.Documents.Staging;

public interface ILegalDocumentStagedContent : IAsyncDisposable
{
    Stream Content { get; }

    long ContentLength { get; }

    ReadOnlyMemory<byte> ContentHashSha256 { get; }
}
