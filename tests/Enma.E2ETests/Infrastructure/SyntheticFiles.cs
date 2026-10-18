using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Enma.E2ETests.Infrastructure;

// Fictitious files built in memory for document journeys: a decodable PNG and
// a well-formed one-page PDF that pass the server content inspection, and a
// malformed PDF that does not. None carries any real content.
public static class SyntheticFiles
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // A 2×2 8-bit RGB image.
    public static byte[] CreatePng()
    {
        const int width = 2;
        const int height = 2;

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 2;

        using var scanlines = new MemoryStream();
        using (var zlib = new ZLibStream(scanlines, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write([0x1F, 0x6F, 0xB4, 0xF2, 0xC1, 0x3D]);
            }
        }

        using var png = new MemoryStream();
        png.Write(PngSignature);
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", scanlines.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    // One empty page, with a cross-reference table whose offsets are exact.
    public static byte[] CreatePdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
        ];

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        int crossReferenceOffset = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        pdf.Append($"startxref\n{crossReferenceOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // A PDF header followed by a truncated body: no startxref, no %%EOF.
    public static byte[] CreateMalformedPdf()
    {
        return "%PDF-1.7\n1 0 obj\n<< /Type /Catalog"u8.ToArray();
    }

    private static void WritePngChunk(Stream png, string type, byte[] data)
    {
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        Span<byte> number = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        png.Write(number);
        png.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
        png.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
