using Amazon.S3.Model;
using Enma.Application.Documents.Storage;

namespace Enma.Infrastructure.Documents.Storage;

public sealed class S3LegalDocumentStorageReadHandle
    : ILegalDocumentStorageReadHandle
{
    private GetObjectResponse? response;

    public S3LegalDocumentStorageReadHandle(GetObjectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        this.response = response;
    }

    public Stream Content =>
        GetActiveResponse().ResponseStream;

    public long ContentLength =>
        GetActiveResponse().ContentLength;

    public ValueTask DisposeAsync()
    {
        GetAndClearResponse()?.Dispose();
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    private GetObjectResponse GetActiveResponse()
    {
        return response
            ?? throw new ObjectDisposedException(
                nameof(S3LegalDocumentStorageReadHandle));
    }

    private GetObjectResponse? GetAndClearResponse()
    {
        GetObjectResponse? current = response;
        response = null;

        return current;
    }
}
