using System.Buffers;
using System.Security.Cryptography;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;

namespace Enma.Infrastructure.Documents.Staging;

public sealed class TempFileLegalDocumentContentStager
    : ILegalDocumentContentStager
{
    private const int BufferSize = 81_920;

    private static readonly string StagingDirectoryPath = Path.Combine(
        Path.GetTempPath(),
        "enma",
        "document-staging");

    public async Task<ILegalDocumentStagedContent> StageAsync(
        Stream source,
        long declaredContentLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead)
        {
            throw new ArgumentException(
                "The document staging source stream must be readable.",
                nameof(source));
        }

        ValidateDeclaredContentLength(declaredContentLength);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream? stagedContent = null;

        try
        {
            stagedContent = CreateTemporaryContentStream();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                using IncrementalHash contentHash =
                    IncrementalHash.CreateHash(
                        HashAlgorithmName.SHA256);

                long actualContentLength = 0;

                while (true)
                {
                    int permittedReadLength = (int)Math.Min(
                        buffer.Length,
                        LegalDocumentUploadPolicy.MaximumFileSizeBytes
                            - actualContentLength
                            + 1);

                    int bytesRead = await source.ReadAsync(
                        buffer.AsMemory(
                            0,
                            permittedReadLength),
                        cancellationToken);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    actualContentLength += bytesRead;

                    if (actualContentLength
                        > LegalDocumentUploadPolicy.MaximumFileSizeBytes)
                    {
                        throw new LegalDocumentUploadRejectedException(
                            LegalDocumentUploadRejectionReason.FileTooLarge);
                    }

                    if (actualContentLength > declaredContentLength)
                    {
                        throw new LegalDocumentUploadRejectedException(
                            LegalDocumentUploadRejectionReason.ContentLengthMismatch);
                    }

                    contentHash.AppendData(
                        buffer,
                        0,
                        bytesRead);

                    await stagedContent.WriteAsync(
                        buffer.AsMemory(
                            0,
                            bytesRead),
                        cancellationToken);
                }

                if (actualContentLength == 0)
                {
                    throw new LegalDocumentUploadRejectedException(
                        LegalDocumentUploadRejectionReason.EmptyFile);
                }

                if (actualContentLength != declaredContentLength)
                {
                    throw new LegalDocumentUploadRejectedException(
                        LegalDocumentUploadRejectionReason.ContentLengthMismatch);
                }

                await stagedContent.FlushAsync(cancellationToken);
                stagedContent.Position = 0;

                byte[] contentHashSha256 =
                    contentHash.GetHashAndReset();

                var result = new TempFileLegalDocumentStagedContent(
                    stagedContent,
                    actualContentLength,
                    contentHashSha256);

                stagedContent = null;
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LegalDocumentUploadRejectedException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new LegalDocumentContentStagingUnavailableException();
        }
        catch (UnauthorizedAccessException)
        {
            throw new LegalDocumentContentStagingUnavailableException();
        }
        finally
        {
            if (stagedContent is not null)
            {
                await stagedContent.DisposeAsync();
            }
        }
    }

    private static void ValidateDeclaredContentLength(
        long declaredContentLength)
    {
        if (declaredContentLength <= 0)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.EmptyFile);
        }

        if (declaredContentLength
            > LegalDocumentUploadPolicy.MaximumFileSizeBytes)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.FileTooLarge);
        }
    }

    private static FileStream CreateTemporaryContentStream()
    {
        Directory.CreateDirectory(StagingDirectoryPath);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                StagingDirectoryPath,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
        }

        byte[] randomNameBytes =
            RandomNumberGenerator.GetBytes(16);
        string randomName =
            Convert.ToHexString(randomNameBytes)
                .ToLowerInvariant();

        string stagingPath = Path.Combine(
            StagingDirectoryPath,
            $"{randomName}.tmp");

        var options = new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.DeleteOnClose
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite;
        }

        return new FileStream(
            stagingPath,
            options);
    }
}
