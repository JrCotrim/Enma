using System.Buffers.Binary;
using System.Text;
using Enma.Application.Documents.Inspection;

namespace Enma.Infrastructure.Documents.Inspection;

public sealed class LegalDocumentContentInspector
    : ILegalDocumentContentInspector
{
    private const int PdfTailInspectionBytes = 1_024;
    private const int MaximumJpegHeaderSegments = 1_024;

    private static readonly byte[] PdfHeaderPrefix =
        "%PDF-"u8.ToArray();
    private static readonly byte[] PdfStartXref =
        "startxref"u8.ToArray();
    private static readonly byte[] PdfEndOfFile =
        "%%EOF"u8.ToArray();

    private static readonly byte[] PngSignature =
    [
        0x89,
        0x50,
        0x4E,
        0x47,
        0x0D,
        0x0A,
        0x1A,
        0x0A
    ];

    private static readonly byte[] PngIend =
    [
        0x00,
        0x00,
        0x00,
        0x00,
        0x49,
        0x45,
        0x4E,
        0x44,
        0xAE,
        0x42,
        0x60,
        0x82
    ];

    public async Task InspectAsync(
        Stream content,
        long contentLength,
        LegalDocumentFileType fileType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException(
                "The document inspection stream must be readable and seekable.",
                nameof(content));
        }

        ValidateContentLength(contentLength);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (content.Length != contentLength)
            {
                RejectInvalidContent();
            }

            content.Position = 0;

            switch (fileType)
            {
                case LegalDocumentFileType.Pdf:
                    await InspectPdfAsync(
                        content,
                        contentLength,
                        cancellationToken);
                    break;

                case LegalDocumentFileType.Png:
                    await InspectPngAsync(
                        content,
                        contentLength,
                        cancellationToken);
                    break;

                case LegalDocumentFileType.Jpeg:
                    await InspectJpegAsync(
                        content,
                        contentLength,
                        cancellationToken);
                    break;

                case LegalDocumentFileType.WordOpenXml:
                case LegalDocumentFileType.ExcelOpenXml:
                    await OpenXmlPackageInspector.InspectAsync(
                        content,
                        contentLength,
                        fileType,
                        cancellationToken);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(fileType),
                        fileType,
                        "Unknown legal document file type.");
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
            throw new LegalDocumentContentInspectionUnavailableException();
        }
        catch (UnauthorizedAccessException)
        {
            throw new LegalDocumentContentInspectionUnavailableException();
        }
        finally
        {
            if (content.CanSeek)
            {
                try
                {
                    content.Position = 0;
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }
    }

    private static async Task InspectPdfAsync(
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength < 20)
        {
            RejectInvalidContent();
        }

        byte[] header = new byte[8];
        await ReadExactlyOrRejectAsync(
            content,
            header,
            cancellationToken);

        if (!header.AsSpan(0, PdfHeaderPrefix.Length)
                .SequenceEqual(PdfHeaderPrefix)
            || (header[5] != (byte)'1'
                && header[5] != (byte)'2')
            || header[6] != (byte)'.'
            || header[7] < (byte)'0'
            || header[7] > (byte)'9')
        {
            RejectInvalidContent();
        }

        int tailLength = (int)Math.Min(
            contentLength,
            PdfTailInspectionBytes);

        byte[] tail = new byte[tailLength];
        content.Position = contentLength - tailLength;

        await ReadExactlyOrRejectAsync(
            content,
            tail,
            cancellationToken);

        int eofIndex = LastIndexOf(
            tail,
            PdfEndOfFile);

        if (eofIndex < 0
            || !ContainsOnlyPdfTrailingWhitespace(
                tail.AsSpan(
                    eofIndex + PdfEndOfFile.Length)))
        {
            RejectInvalidContent();
        }

        int startXrefIndex = LastIndexOf(
            tail.AsSpan(
                0,
                eofIndex),
            PdfStartXref);

        if (startXrefIndex < 0)
        {
            RejectInvalidContent();
        }

        ReadOnlySpan<byte> startXrefTail = tail.AsSpan(
            startXrefIndex + PdfStartXref.Length,
            eofIndex
                - startXrefIndex
                - PdfStartXref.Length);

        if (!ContainsPdfStartXrefOffset(startXrefTail))
        {
            RejectInvalidContent();
        }
    }

    private static async Task InspectPngAsync(
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        const int pngHeaderLength = 33;

        if (contentLength < pngHeaderLength + PngIend.Length)
        {
            RejectInvalidContent();
        }

        byte[] header = new byte[pngHeaderLength];
        await ReadExactlyOrRejectAsync(
            content,
            header,
            cancellationToken);

        ReadOnlySpan<byte> headerSpan = header;

        if (!headerSpan[..PngSignature.Length]
                .SequenceEqual(PngSignature)
            || BinaryPrimitives.ReadUInt32BigEndian(
                headerSpan.Slice(8, 4)) != 13
            || !headerSpan.Slice(12, 4)
                .SequenceEqual("IHDR"u8))
        {
            RejectInvalidContent();
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(
            headerSpan.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(
            headerSpan.Slice(20, 4));
        byte bitDepth = headerSpan[24];
        byte colorType = headerSpan[25];
        byte compressionMethod = headerSpan[26];
        byte filterMethod = headerSpan[27];
        byte interlaceMethod = headerSpan[28];

        if (width == 0
            || width > (uint)int.MaxValue
            || height == 0
            || height > (uint)int.MaxValue
            || !IsValidPngBitDepth(
                bitDepth,
                colorType)
            || compressionMethod != 0
            || filterMethod != 0
            || interlaceMethod > 1)
        {
            RejectInvalidContent();
        }

        uint expectedIhdrCrc = BinaryPrimitives.ReadUInt32BigEndian(
            headerSpan.Slice(29, 4));
        uint actualIhdrCrc = ComputePngCrc32(
            headerSpan.Slice(12, 17));

        if (expectedIhdrCrc != actualIhdrCrc)
        {
            RejectInvalidContent();
        }

        byte[] iend = new byte[PngIend.Length];
        content.Position = contentLength - PngIend.Length;

        await ReadExactlyOrRejectAsync(
            content,
            iend,
            cancellationToken);

        if (!iend.AsSpan().SequenceEqual(PngIend))
        {
            RejectInvalidContent();
        }
    }

    private static async Task InspectJpegAsync(
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength < 16)
        {
            RejectInvalidContent();
        }

        byte[] twoBytes = new byte[2];

        await ReadExactlyOrRejectAsync(
            content,
            twoBytes,
            cancellationToken);

        if (twoBytes[0] != 0xFF
            || twoBytes[1] != 0xD8)
        {
            RejectInvalidContent();
        }

        content.Position = contentLength - 2;

        await ReadExactlyOrRejectAsync(
            content,
            twoBytes,
            cancellationToken);

        if (twoBytes[0] != 0xFF
            || twoBytes[1] != 0xD9)
        {
            RejectInvalidContent();
        }

        content.Position = 2;

        bool foundStartOfFrame = false;
        bool foundStartOfScan = false;

        for (int segmentCount = 0;
             segmentCount < MaximumJpegHeaderSegments;
             segmentCount++)
        {
            byte marker = await ReadJpegMarkerAsync(
                content,
                contentLength,
                cancellationToken);

            if (marker == 0xD9)
            {
                break;
            }

            if (IsStandaloneJpegMarker(marker))
            {
                continue;
            }

            ushort segmentLength =
                await ReadUInt16BigEndianAsync(
                    content,
                    cancellationToken);

            if (segmentLength < 2)
            {
                RejectInvalidContent();
            }

            long segmentDataLength = segmentLength - 2L;

            if (content.Position + segmentDataLength
                > contentLength - 2)
            {
                RejectInvalidContent();
            }

            if (IsJpegStartOfFrameMarker(marker))
            {
                await ValidateJpegStartOfFrameAsync(
                    content,
                    segmentLength,
                    cancellationToken);
                foundStartOfFrame = true;
                continue;
            }

            if (marker == 0xDA)
            {
                if (!foundStartOfFrame)
                {
                    RejectInvalidContent();
                }

                await ValidateJpegStartOfScanAsync(
                    content,
                    segmentLength,
                    cancellationToken);

                foundStartOfScan = true;
                break;
            }

            content.Position += segmentDataLength;
        }

        if (!foundStartOfFrame
            || !foundStartOfScan)
        {
            RejectInvalidContent();
        }
    }

    private static async Task ValidateJpegStartOfFrameAsync(
        Stream content,
        ushort segmentLength,
        CancellationToken cancellationToken)
    {
        if (segmentLength < 11)
        {
            RejectInvalidContent();
        }

        byte[] fixedHeader = new byte[6];
        await ReadExactlyOrRejectAsync(
            content,
            fixedHeader,
            cancellationToken);

        byte samplePrecision = fixedHeader[0];
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(
            fixedHeader.AsSpan(1, 2));
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(
            fixedHeader.AsSpan(3, 2));
        byte componentCount = fixedHeader[5];

        int expectedSegmentLength =
            8 + (3 * componentCount);

        if ((samplePrecision != 8
                && samplePrecision != 12)
            || width == 0
            || height == 0
            || componentCount is < 1 or > 4
            || segmentLength != expectedSegmentLength)
        {
            RejectInvalidContent();
        }

        long remainingComponentBytes =
            segmentLength - 2L - fixedHeader.Length;

        content.Position += remainingComponentBytes;
    }

    private static async Task ValidateJpegStartOfScanAsync(
        Stream content,
        ushort segmentLength,
        CancellationToken cancellationToken)
    {
        if (segmentLength < 8)
        {
            RejectInvalidContent();
        }

        byte[] componentCountBuffer = new byte[1];
        await ReadExactlyOrRejectAsync(
            content,
            componentCountBuffer,
            cancellationToken);

        byte componentCount = componentCountBuffer[0];
        int expectedSegmentLength =
            6 + (2 * componentCount);

        if (componentCount is < 1 or > 4
            || segmentLength != expectedSegmentLength)
        {
            RejectInvalidContent();
        }

        long remainingBytes =
            segmentLength - 3L;

        content.Position += remainingBytes;
    }

    private static async Task<byte> ReadJpegMarkerAsync(
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        byte[] singleByte = new byte[1];

        while (content.Position < contentLength - 2)
        {
            await ReadExactlyOrRejectAsync(
                content,
                singleByte,
                cancellationToken);

            if (singleByte[0] != 0xFF)
            {
                RejectInvalidContent();
            }

            do
            {
                await ReadExactlyOrRejectAsync(
                    content,
                    singleByte,
                    cancellationToken);
            }
            while (singleByte[0] == 0xFF);

            if (singleByte[0] == 0x00)
            {
                RejectInvalidContent();
            }

            return singleByte[0];
        }

        RejectInvalidContent();
        return 0;
    }

    private static async Task<ushort> ReadUInt16BigEndianAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2];
        await ReadExactlyOrRejectAsync(
            content,
            buffer,
            cancellationToken);

        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static async Task ReadExactlyOrRejectAsync(
        Stream content,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int bytesRead = await content.ReadAsync(
                buffer[totalRead..],
                cancellationToken);

            if (bytesRead == 0)
            {
                RejectInvalidContent();
            }

            totalRead += bytesRead;
        }
    }

    private static void ValidateContentLength(long contentLength)
    {
        if (contentLength <= 0)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.EmptyFile);
        }

        if (contentLength
            > LegalDocumentUploadPolicy.MaximumFileSizeBytes)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.FileTooLarge);
        }
    }

    private static bool ContainsOnlyPdfTrailingWhitespace(
        ReadOnlySpan<byte> trailingBytes)
    {
        foreach (byte value in trailingBytes)
        {
            if (value is not (
                (byte)' '
                or (byte)'\t'
                or (byte)'\r'
                or (byte)'\n'
                or 0x0C))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsPdfStartXrefOffset(
        ReadOnlySpan<byte> value)
    {
        int index = 0;

        while (index < value.Length
            && IsPdfWhitespace(value[index]))
        {
            index++;
        }

        int digitStart = index;

        while (index < value.Length
            && value[index] is >= (byte)'0' and <= (byte)'9')
        {
            index++;
        }

        if (digitStart == index)
        {
            return false;
        }

        while (index < value.Length)
        {
            if (!IsPdfWhitespace(value[index]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static bool IsPdfWhitespace(byte value)
    {
        return value is
            (byte)' '
            or (byte)'\t'
            or (byte)'\r'
            or (byte)'\n'
            or 0x0C;
    }

    private static bool IsValidPngBitDepth(
        byte bitDepth,
        byte colorType)
    {
        return colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };
    }

    private static uint ComputePngCrc32(
        ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                uint mask =
                    (uint)-(int)(crc & 1);

                crc = (crc >> 1)
                    ^ (0xEDB88320u & mask);
            }
        }

        return ~crc;
    }

    private static bool IsStandaloneJpegMarker(byte marker)
    {
        return marker == 0x01
            || marker is >= 0xD0 and <= 0xD8;
    }

    private static bool IsJpegStartOfFrameMarker(byte marker)
    {
        return marker is
            0xC0
            or 0xC1
            or 0xC2
            or 0xC3
            or 0xC5
            or 0xC6
            or 0xC7
            or 0xC9
            or 0xCA
            or 0xCB
            or 0xCD
            or 0xCE
            or 0xCF;
    }

    private static int LastIndexOf(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> value)
    {
        if (value.Length == 0
            || source.Length < value.Length)
        {
            return -1;
        }

        for (int index = source.Length - value.Length;
             index >= 0;
             index--)
        {
            if (source.Slice(
                    index,
                    value.Length)
                .SequenceEqual(value))
            {
                return index;
            }
        }

        return -1;
    }

    private static void RejectInvalidContent()
    {
        throw new LegalDocumentUploadRejectedException(
            LegalDocumentUploadRejectionReason.InvalidFileContent);
    }
}
