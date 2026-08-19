using System.IO.Compression;
using System.Xml;
using Enma.Application.Documents.Inspection;

namespace Enma.Infrastructure.Documents.Inspection;

internal static class OpenXmlPackageInspector
{
    private const int MaximumEntryCount = 4_096;
    private const long MaximumSingleEntryLength = 64L * 1024L * 1024L;
    private const long MaximumTotalUncompressedLength = 128L * 1024L * 1024L;
    private const long MinimumLengthForCompressionRatioCheck = 1L * 1024L * 1024L;
    private const long MaximumCompressionRatio = 500;
    private const int MaximumControlXmlBytes = 1 * 1024 * 1024;

    private const string ContentTypesEntryName = "[Content_Types].xml";
    private const string RootRelationshipsEntryName = "_rels/.rels";

    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TransitionalOfficeDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string StrictOfficeDocumentRelationshipType =
        "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";

    private static readonly byte[] ZipLocalFileHeaderSignature =
    [
        0x50,
        0x4B,
        0x03,
        0x04
    ];

    public static async Task InspectAsync(
        Stream content,
        long contentLength,
        LegalDocumentFileType fileType,
        CancellationToken cancellationToken)
    {
        PackageDescriptor descriptor = GetDescriptor(fileType);

        await ValidateZipSignatureAsync(
            content,
            cancellationToken);

        content.Position = 0;

        try
        {
            using var archive = new ZipArchive(
                content,
                ZipArchiveMode.Read,
                leaveOpen: true);

            IReadOnlyDictionary<string, ZipArchiveEntry> entries =
                ValidateArchiveEntries(archive);

            if (!entries.ContainsKey(ContentTypesEntryName)
                || !entries.ContainsKey(RootRelationshipsEntryName)
                || !entries.ContainsKey(descriptor.MainPartName))
            {
                RejectInvalidContent();
            }

            ZipArchiveEntry contentTypesEntry =
                entries[ContentTypesEntryName];
            ZipArchiveEntry relationshipsEntry =
                entries[RootRelationshipsEntryName];

            RejectMacroBearingPackage(
                entries.Values);

            byte[] contentTypesXml =
                await ReadControlEntryAsync(
                    contentTypesEntry,
                    cancellationToken);

            ValidateContentTypes(
                contentTypesXml,
                descriptor);

            byte[] relationshipsXml =
                await ReadControlEntryAsync(
                    relationshipsEntry,
                    cancellationToken);

            ValidateRootRelationships(
                relationshipsXml,
                descriptor);
        }
        catch (InvalidDataException)
        {
            RejectInvalidContent();
        }
        catch (NotSupportedException)
        {
            RejectInvalidContent();
        }
        catch (XmlException)
        {
            RejectInvalidContent();
        }
    }

    private static async Task ValidateZipSignatureAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        if (content.Length < ZipLocalFileHeaderSignature.Length)
        {
            RejectInvalidContent();
        }

        content.Position = 0;
        byte[] signature =
            new byte[ZipLocalFileHeaderSignature.Length];

        int totalRead = 0;

        while (totalRead < signature.Length)
        {
            int bytesRead = await content.ReadAsync(
                signature.AsMemory(totalRead),
                cancellationToken);

            if (bytesRead == 0)
            {
                RejectInvalidContent();
            }

            totalRead += bytesRead;
        }

        if (!signature.AsSpan()
                .SequenceEqual(ZipLocalFileHeaderSignature))
        {
            RejectInvalidContent();
        }
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry>
        ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntryCount)
        {
            RejectInvalidContent();
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(
            StringComparer.Ordinal);
        var caseInsensitiveNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        long totalUncompressedLength = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);

            if (!caseInsensitiveNames.Add(entry.FullName)
                || !entries.TryAdd(
                    entry.FullName,
                    entry))
            {
                RejectInvalidContent();
            }

            if (entry.Length < 0
                || entry.CompressedLength < 0
                || entry.Length > MaximumSingleEntryLength)
            {
                RejectInvalidContent();
            }

            try
            {
                totalUncompressedLength = checked(
                    totalUncompressedLength + entry.Length);
            }
            catch (OverflowException)
            {
                RejectInvalidContent();
            }

            if (totalUncompressedLength
                > MaximumTotalUncompressedLength)
            {
                RejectInvalidContent();
            }

            if (entry.Length >= MinimumLengthForCompressionRatioCheck)
            {
                if (entry.CompressedLength == 0)
                {
                    RejectInvalidContent();
                }

                long maximumAllowedExpandedLength;

                try
                {
                    maximumAllowedExpandedLength = checked(
                        entry.CompressedLength
                        * MaximumCompressionRatio);
                }
                catch (OverflowException)
                {
                    maximumAllowedExpandedLength = long.MaxValue;
                }

                if (entry.Length > maximumAllowedExpandedLength)
                {
                    RejectInvalidContent();
                }
            }
        }

        return entries;
    }

    private static void ValidateEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)
            || entryName.StartsWith('/')
            || entryName.Contains('\\')
            || entryName.Contains(':')
            || ContainsDisallowedUnicode(entryName))
        {
            RejectInvalidContent();
        }

        bool isDirectory = entryName.EndsWith('/');

        string pathToValidate = isDirectory
            ? entryName[..^1]
            : entryName;

        if (string.IsNullOrEmpty(pathToValidate))
        {
            RejectInvalidContent();
        }

        string[] segments = pathToValidate.Split(
            '/',
            StringSplitOptions.None);

        if (segments.Any(
                segment =>
                    string.IsNullOrEmpty(segment)
                    || segment is "." or ".."))
        {
            RejectInvalidContent();
        }
    }

    private static bool ContainsDisallowedUnicode(string value)
    {
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            System.Globalization.UnicodeCategory category =
                System.Text.Rune.GetUnicodeCategory(rune);

            if (category is
                System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate)
            {
                return true;
            }
        }

        return false;
    }

    private static void RejectMacroBearingPackage(
        IEnumerable<ZipArchiveEntry> entries)
    {
        foreach (ZipArchiveEntry entry in entries)
        {
            string entryName = entry.FullName;

            if (entryName.EndsWith(
                    "/vbaProject.bin",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    entryName,
                    "vbaProject.bin",
                    StringComparison.OrdinalIgnoreCase))
            {
                RejectInvalidContent();
            }
        }
    }

    private static async Task<byte[]> ReadControlEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 1
            || entry.Length > MaximumControlXmlBytes)
        {
            RejectInvalidContent();
        }

        await using Stream entryStream = entry.Open();
        using var buffer = new MemoryStream(
            capacity: (int)Math.Min(
                entry.Length,
                MaximumControlXmlBytes));

        byte[] chunk = new byte[16 * 1024];
        int totalRead = 0;

        while (true)
        {
            int bytesRead = await entryStream.ReadAsync(
                chunk,
                cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;

            if (totalRead > MaximumControlXmlBytes)
            {
                RejectInvalidContent();
            }

            await buffer.WriteAsync(
                chunk.AsMemory(
                    0,
                    bytesRead),
                cancellationToken);
        }

        if (totalRead == 0
            || totalRead != entry.Length)
        {
            RejectInvalidContent();
        }

        return buffer.ToArray();
    }

    private static void ValidateContentTypes(
        byte[] xml,
        PackageDescriptor descriptor)
    {
        using XmlReader reader = CreateSecureXmlReader(xml);

        bool foundExpectedMainPart = false;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || reader.NamespaceURI != ContentTypesNamespace)
            {
                continue;
            }

            string? contentType = reader.GetAttribute(
                "ContentType");

            if (!string.IsNullOrEmpty(contentType)
                && IsMacroContentType(contentType))
            {
                RejectInvalidContent();
            }

            if (reader.LocalName != "Override")
            {
                continue;
            }

            string? partName = reader.GetAttribute(
                "PartName");

            if (!string.Equals(
                    partName,
                    descriptor.MainPartPath,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (foundExpectedMainPart
                || !string.Equals(
                    contentType,
                    descriptor.MainPartContentType,
                    StringComparison.Ordinal))
            {
                RejectInvalidContent();
            }

            foundExpectedMainPart = true;
        }

        if (!foundExpectedMainPart)
        {
            RejectInvalidContent();
        }
    }

    private static void ValidateRootRelationships(
        byte[] xml,
        PackageDescriptor descriptor)
    {
        using XmlReader reader = CreateSecureXmlReader(xml);

        int officeDocumentRelationshipCount = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || reader.NamespaceURI != RelationshipsNamespace
                || reader.LocalName != "Relationship")
            {
                continue;
            }

            string? relationshipType = reader.GetAttribute(
                "Type");

            if (!IsOfficeDocumentRelationshipType(
                    relationshipType))
            {
                continue;
            }

            officeDocumentRelationshipCount++;

            string? target = reader.GetAttribute(
                "Target");
            string? targetMode = reader.GetAttribute(
                "TargetMode");

            if (officeDocumentRelationshipCount != 1
                || string.Equals(
                    targetMode,
                    "External",
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(target)
                || target.Contains('\\')
                || target.Contains(
                    "..",
                    StringComparison.Ordinal)
                || !string.Equals(
                    target.TrimStart('/'),
                    descriptor.MainPartName,
                    StringComparison.Ordinal))
            {
                RejectInvalidContent();
            }
        }

        if (officeDocumentRelationshipCount != 1)
        {
            RejectInvalidContent();
        }
    }

    private static XmlReader CreateSecureXmlReader(byte[] xml)
    {
        var stream = new MemoryStream(
            xml,
            writable: false);

        return XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument =
                    MaximumControlXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
    }

    private static bool IsMacroContentType(string contentType)
    {
        return contentType.Contains(
                "macroEnabled",
                StringComparison.OrdinalIgnoreCase)
            || contentType.Contains(
                "vbaProject",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficeDocumentRelationshipType(
        string? relationshipType)
    {
        return string.Equals(
                relationshipType,
                TransitionalOfficeDocumentRelationshipType,
                StringComparison.Ordinal)
            || string.Equals(
                relationshipType,
                StrictOfficeDocumentRelationshipType,
                StringComparison.Ordinal);
    }

    private static PackageDescriptor GetDescriptor(
        LegalDocumentFileType fileType)
    {
        return fileType switch
        {
            LegalDocumentFileType.WordOpenXml =>
                new PackageDescriptor(
                    "word/document.xml",
                    "/word/document.xml",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),

            LegalDocumentFileType.ExcelOpenXml =>
                new PackageDescriptor(
                    "xl/workbook.xml",
                    "/xl/workbook.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(fileType),
                    fileType,
                    "The file type is not an Open XML package.")
        };
    }

    private static void RejectInvalidContent()
    {
        throw new LegalDocumentUploadRejectedException(
            LegalDocumentUploadRejectionReason.InvalidFileContent);
    }

    private sealed record PackageDescriptor(
        string MainPartName,
        string MainPartPath,
        string MainPartContentType);
}
