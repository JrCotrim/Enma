using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Enma.Application.Documents.Storage;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Documents.Storage;

public sealed class S3LegalDocumentStorage : ILegalDocumentStorage
{
    private readonly IAmazonS3 s3Client;
    private readonly string bucketName;

    public S3LegalDocumentStorage(
        IAmazonS3 s3Client,
        IOptions<DocumentStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(s3Client);
        ArgumentNullException.ThrowIfNull(options);

        this.s3Client = s3Client;
        bucketName = options.Value.BucketName;
    }

    public async Task StoreAsync(
        LegalDocumentStorageObjectKey objectKey,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        ValidateInputStream(content, contentLength);

        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey.Value,
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            IfNoneMatch = "*"
        };

        try
        {
            await s3Client.PutObjectAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsObjectKeyConflict(exception))
        {
            throw new LegalDocumentStorageObjectKeyConflictException();
        }
        catch (AmazonServiceException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
        catch (AmazonClientException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
    }

    public async Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
        LegalDocumentStorageObjectKey objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var request = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey.Value
        };

        try
        {
            GetObjectResponse response = await s3Client.GetObjectAsync(
                request,
                cancellationToken);

            return new S3LegalDocumentStorageReadHandle(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            throw new LegalDocumentStorageObjectNotFoundException();
        }
        catch (AmazonServiceException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
        catch (AmazonClientException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
    }

    public async Task DeleteIfExistsAsync(
        LegalDocumentStorageObjectKey objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var request = new DeleteObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey.Value
        };

        try
        {
            await s3Client.DeleteObjectAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            // Compensation is intentionally idempotent.
        }
        catch (AmazonServiceException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
        catch (AmazonClientException)
        {
            throw new LegalDocumentStorageUnavailableException();
        }
    }

    private static void ValidateInputStream(
        Stream content,
        long contentLength)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The document storage input stream must be readable.",
                nameof(content));
        }

        if (!content.CanSeek)
        {
            throw new ArgumentException(
                "The document storage input stream must be seekable.",
                nameof(content));
        }

        if (contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentLength),
                "The document storage content length cannot be negative.");
        }

        long remainingLength = content.Length - content.Position;

        if (remainingLength != contentLength)
        {
            throw new ArgumentException(
                "The declared document storage content length must match the remaining stream length.",
                nameof(contentLength));
        }
    }

    private static bool IsObjectKeyConflict(AmazonS3Exception exception)
    {
        return exception.StatusCode is HttpStatusCode.PreconditionFailed
            or HttpStatusCode.Conflict;
    }

    private static bool IsObjectNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound
            || string.Equals(
                exception.ErrorCode,
                "NoSuchKey",
                StringComparison.Ordinal);
    }
}
