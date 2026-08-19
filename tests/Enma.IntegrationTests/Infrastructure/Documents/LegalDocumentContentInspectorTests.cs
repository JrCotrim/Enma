using Enma.Application.Documents.Inspection;
using Enma.Infrastructure.Documents.Inspection;

namespace Enma.IntegrationTests.Infrastructure.Documents;

public sealed class LegalDocumentContentInspectorTests
{
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";

    private const string ValidJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==";

    private readonly LegalDocumentContentInspector inspector = new();

    [Fact]
    public async Task InspectAsync_ValidPdf_AcceptsAndRewinds()
    {
        byte[] content = CreateValidPdf();
        using var stream = new MemoryStream(
            content,
            writable: false);

        await inspector.InspectAsync(
            stream,
            content.LongLength,
            LegalDocumentFileType.Pdf,
            CancellationToken.None);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_PdfWithoutHeader_Rejects()
    {
        byte[] content = CreateValidPdf();
        content[0] = (byte)'X';

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Pdf);
    }

    [Fact]
    public async Task InspectAsync_PdfWithoutStartXref_Rejects()
    {
        byte[] content =
            "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n"u8.ToArray();

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Pdf);
    }

    [Fact]
    public async Task InspectAsync_PdfWithTrailingPayloadAfterEof_Rejects()
    {
        byte[] content =
            "%PDF-1.7\n1 0 obj\n<<>>\nendobj\nstartxref\n9\n%%EOF\n<script>"u8.ToArray();

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Pdf);
    }

    [Fact]
    public async Task InspectAsync_ValidPng_AcceptsAndRewinds()
    {
        byte[] content =
            Convert.FromBase64String(ValidPngBase64);
        using var stream = new MemoryStream(
            content,
            writable: false);

        await inspector.InspectAsync(
            stream,
            content.LongLength,
            LegalDocumentFileType.Png,
            CancellationToken.None);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_PngWithInvalidSignature_Rejects()
    {
        byte[] content =
            Convert.FromBase64String(ValidPngBase64);
        content[1] = 0x00;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Png);
    }

    [Fact]
    public async Task InspectAsync_PngWithInvalidIhdrCrc_Rejects()
    {
        byte[] content =
            Convert.FromBase64String(ValidPngBase64);
        content[29] ^= 0xFF;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Png);
    }

    [Fact]
    public async Task InspectAsync_PngWithoutIend_Rejects()
    {
        byte[] content =
            Convert.FromBase64String(ValidPngBase64);
        content[^1] = 0x00;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Png);
    }

    [Fact]
    public async Task InspectAsync_ValidJpeg_AcceptsAndRewinds()
    {
        byte[] content =
            Convert.FromBase64String(ValidJpegBase64);
        using var stream = new MemoryStream(
            content,
            writable: false);

        await inspector.InspectAsync(
            stream,
            content.LongLength,
            LegalDocumentFileType.Jpeg,
            CancellationToken.None);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_JpegWithoutSoi_Rejects()
    {
        byte[] content =
            Convert.FromBase64String(ValidJpegBase64);
        content[0] = 0x00;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Jpeg);
    }

    [Fact]
    public async Task InspectAsync_JpegWithoutEoi_Rejects()
    {
        byte[] content =
            Convert.FromBase64String(ValidJpegBase64);
        content[^1] = 0x00;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Jpeg);
    }

    [Fact]
    public async Task InspectAsync_JpegWithoutStartOfFrame_Rejects()
    {
        byte[] content =
        [
            0xFF, 0xD8,
            0xFF, 0xDA,
            0x00, 0x08,
            0x01,
            0x01, 0x00,
            0x00, 0x3F, 0x00,
            0x00,
            0xFF, 0xD9
        ];

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.Jpeg);
    }

    [Fact]
    public async Task InspectAsync_ContentLengthMismatch_RejectsAndRewinds()
    {
        byte[] content = CreateValidPdf();
        using var stream = new MemoryStream(
            content,
            writable: false);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => inspector.InspectAsync(
                    stream,
                    content.LongLength - 1,
                    LegalDocumentFileType.Pdf,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.InvalidFileContent,
            exception.Reason);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_PreCanceledRequest_PropagatesCancellationAndRewinds()
    {
        byte[] content = CreateValidPdf();
        using var stream = new MemoryStream(
            content,
            writable: false);
        using var cancellationTokenSource =
            new CancellationTokenSource();

        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.InspectAsync(
                stream,
                content.LongLength,
                LegalDocumentFileType.Pdf,
                cancellationTokenSource.Token));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_InvalidContentException_DoesNotEchoBytes()
    {
        const string sensitiveMarker =
            "CLIENT-SECRET-CASE-NAME";

        byte[] content =
            System.Text.Encoding.UTF8.GetBytes(
                $"{sensitiveMarker} invalid pdf content");
        using var stream = new MemoryStream(
            content,
            writable: false);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => inspector.InspectAsync(
                    stream,
                    content.LongLength,
                    LegalDocumentFileType.Pdf,
                    CancellationToken.None));

        Assert.DoesNotContain(
            sensitiveMarker,
            exception.Message,
            StringComparison.Ordinal);
    }

    private async Task AssertInvalidAsync(
        byte[] content,
        LegalDocumentFileType fileType)
    {
        using var stream = new MemoryStream(
            content,
            writable: false);

        LegalDocumentUploadRejectedException exception =
            await Assert.ThrowsAsync<LegalDocumentUploadRejectedException>(
                () => inspector.InspectAsync(
                    stream,
                    content.LongLength,
                    fileType,
                    CancellationToken.None));

        Assert.Equal(
            LegalDocumentUploadRejectionReason.InvalidFileContent,
            exception.Reason);
        Assert.Equal(0, stream.Position);
    }

    private static byte[] CreateValidPdf()
    {
        return "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n9\n%%EOF\n"u8.ToArray();
    }
}
