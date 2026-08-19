namespace Enma.Application.Documents.Storage;

public interface ILegalDocumentStorageReadHandle : IAsyncDisposable
{
    Stream Content { get; }

    long ContentLength { get; }
}