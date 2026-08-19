using System.Security.Cryptography;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Infrastructure.Documents.Staging;

namespace Enma.IntegrationTests.Infrastructure.Documents;

public sealed class TempFileLegalDocumentContentStagerTests
{
    private readonly TempFileLegalDocumentContentStager stager = new();

    [Fact]
    public async Task StageAsync_NonSeekableContent_StagesExactBytesAndComputesSha256()
    {
        byte[] payload =
            "ENMA bounded legal-document staging"u8.ToArray();
        await using var source =
            new NonSeekableReadStream(payload);

        await using ILegalDocumentStagedContent staged =
            await stager.StageAsync(
                source,
                payload.LongLength,
                CancellationToken.None);

        Assert.True(source.CanRead);
        Assert.True(staged.Content.CanRead);
        Assert.True(staged.Content.CanSeek);
        Assert.Equal(0, staged.Content.Position);
        Assert.Equal(payload.LongLength, staged.ContentLength);
        Assert.Equal(
            SHA256.HashData(payload),
            staged.ContentHashSha256.ToArray());

        using var copy = new MemoryStream();
        await staged.Content.CopyToAsync(
            copy,
            CancellationToken.None);

        Assert.Equal(payload, copy.ToArray());
    }

    [Fact]
    public async Task StageAsync_DisposedStagedContent_BecomesUnavailable()
    {
        byte[] payload = "dispose test"u8.ToArray();
        using var source = new MemoryStream(
            payload,
            writable: false);

        ILegalDocumentStagedContent staged =
            await stager.StageAsync(
                source,
                payload.LongLength,
                CancellationToken.None);

        await staged.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => _ = staged.Content);
        Assert.Throws<ObjectDisposedException>(
            () => _ = staged.ContentHashSha256);
    }

    [Fact]
    public async Task StageAsync_DeclaredLengthSmallerThanActual_RejectsMismatch()
    {
        byte[] payload = "1234567890"u8.ToArray();
        using var source = new MemoryStream(
            payload,
            writable: false);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => stager.StageAsync(
                    source,
                    payload.LongLength - 1,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.ContentLengthMismatch,
            exception.Reason);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task StageAsync_DeclaredLengthGreaterThanActual_RejectsMismatch()
    {
        byte[] payload = "1234567890"u8.ToArray();
        using var source = new MemoryStream(
            payload,
            writable: false);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => stager.StageAsync(
                    source,
                    payload.LongLength + 1,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.ContentLengthMismatch,
            exception.Reason);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task StageAsync_EmptyActualContent_RejectsEmptyFile()
    {
        using var source = new MemoryStream();

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => stager.StageAsync(
                    source,
                    1,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.EmptyFile,
            exception.Reason);
    }

    [Fact]
    public async Task StageAsync_DeclaredLengthOverMaximum_RejectsBeforeReading()
    {
        await using var source = new ThrowOnReadStream();

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => stager.StageAsync(
                    source,
                    LegalDocumentUploadPolicy.MaximumFileSizeBytes + 1,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.FileTooLarge,
            exception.Reason);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task StageAsync_ActualContentOverMaximum_RejectsAtBound()
    {
        long actualLength =
            LegalDocumentUploadPolicy.MaximumFileSizeBytes + 1;
        await using var source =
            new RepeatingByteReadStream(actualLength);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => stager.StageAsync(
                    source,
                    LegalDocumentUploadPolicy.MaximumFileSizeBytes,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.FileTooLarge,
            exception.Reason);
        Assert.Equal(
            LegalDocumentUploadPolicy.MaximumFileSizeBytes + 1,
            source.BytesRead);
    }

    [Fact]
    public async Task StageAsync_PreCanceledRequest_PropagatesCancellation()
    {
        byte[] payload = "cancel me"u8.ToArray();
        using var source = new MemoryStream(
            payload,
            writable: false);
        using var cancellationTokenSource =
            new CancellationTokenSource();

        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stager.StageAsync(
                source,
                payload.LongLength,
                cancellationTokenSource.Token));

        Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task StageAsync_SourceIoFailure_ReturnsSanitizedStagingFailure()
    {
        await using var source = new FailingReadStream();

        LegalDocumentContentStagingUnavailableException exception =
            await Assert.ThrowsAsync<
                LegalDocumentContentStagingUnavailableException>(
                () => stager.StageAsync(
                    source,
                    1,
                    CancellationToken.None));

        Assert.Equal(
            "Document content staging is temporarily unavailable.",
            exception.Message);
        Assert.DoesNotContain(
            Path.GetTempPath(),
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream inner;

        public NonSeekableReadStream(byte[] content)
        {
            inner = new MemoryStream(
                content,
                writable: false);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            return inner.Read(
                buffer,
                offset,
                count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(
                buffer,
                cancellationToken);
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            ReadCount++;
            throw new InvalidOperationException(
                "The source should not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException(
                "The source should not be read.");
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RepeatingByteReadStream : Stream
    {
        private long remaining;

        public RepeatingByteReadStream(long length)
        {
            remaining = length;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            int bytesToReturn = GetBytesToReturn(count);
            Array.Fill<byte>(
                buffer,
                0x41,
                offset,
                bytesToReturn);
            remaining -= bytesToReturn;
            BytesRead += bytesToReturn;

            return bytesToReturn;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesToReturn =
                GetBytesToReturn(buffer.Length);
            buffer.Span[..bytesToReturn].Fill(0x41);
            remaining -= bytesToReturn;
            BytesRead += bytesToReturn;

            return ValueTask.FromResult(bytesToReturn);
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        private int GetBytesToReturn(int requestedCount)
        {
            if (remaining == 0)
            {
                return 0;
            }

            return (int)Math.Min(
                remaining,
                requestedCount);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new IOException(
                "Synthetic source path C:\\sensitive\\document.pdf failed.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            throw new IOException(
                "Synthetic source path C:\\sensitive\\document.pdf failed.");
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }
    }
}
