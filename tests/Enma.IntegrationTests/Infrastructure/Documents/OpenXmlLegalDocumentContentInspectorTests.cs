using System.IO.Compression;
using System.Text;
using Enma.Application.Documents.Inspection;
using Enma.Infrastructure.Documents.Inspection;

namespace Enma.IntegrationTests.Infrastructure.Documents;

public sealed class OpenXmlLegalDocumentContentInspectorTests
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private readonly LegalDocumentContentInspector inspector = new();

    [Fact]
    public async Task InspectAsync_ValidDocxPackage_AcceptsAndRewinds()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml);

        using var stream = new MemoryStream(
            content,
            writable: false);

        await inspector.InspectAsync(
            stream,
            content.LongLength,
            LegalDocumentFileType.WordOpenXml,
            CancellationToken.None);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_ValidXlsxPackage_AcceptsAndRewinds()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.ExcelOpenXml);

        using var stream = new MemoryStream(
            content,
            writable: false);

        await inspector.InspectAsync(
            stream,
            content.LongLength,
            LegalDocumentFileType.ExcelOpenXml,
            CancellationToken.None);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithoutZipSignature_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml);
        content[0] = 0x00;

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_DocxMissingMainPart_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            includeMainPart: false);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_XlsxWithWrongMainContentType_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.ExcelOpenXml,
            overrideMainContentType:
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.ExcelOpenXml);
    }

    [Fact]
    public async Task InspectAsync_DocxWithMacroEnabledContentType_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            additionalContentType:
                "application/vnd.ms-word.document.macroEnabled.main+xml");

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Theory]
    [InlineData(
        LegalDocumentFileType.WordOpenXml,
        "word/vbaProject.bin")]
    [InlineData(
        LegalDocumentFileType.ExcelOpenXml,
        "xl/vbaProject.bin")]
    public async Task InspectAsync_OpenXmlWithVbaProject_Rejects(
        LegalDocumentFileType fileType,
        string macroEntryName)
    {
        byte[] content = CreatePackage(
            fileType,
            additionalEntries:
            [
                new PackageEntry(
                    macroEntryName,
                    [0x00, 0x01, 0x02])
            ]);

        await AssertInvalidAsync(
            content,
            fileType);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithTraversalEntry_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            additionalEntries:
            [
                new PackageEntry(
                    "../payload.exe",
                    "payload"u8.ToArray())
            ]);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithCaseInsensitiveDuplicateEntry_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            additionalEntries:
            [
                new PackageEntry(
                    "WORD/DOCUMENT.XML",
                    "<duplicate/>"u8.ToArray())
            ]);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithExternalOfficeDocumentRelationship_Rejects()
    {
        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            relationshipTarget:
                "https://example.invalid/document.xml",
            relationshipTargetMode:
                "External");

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithDtdInContentTypes_Rejects()
    {
        const string contentTypesXml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE Types [<!ENTITY xxe SYSTEM "file:///never-read">]>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Override PartName="/word/document.xml"
                        ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
            </Types>
            """;

        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            overrideContentTypesXml:
                contentTypesXml);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithExtremeCompressionRatio_Rejects()
    {
        byte[] repetitivePayload =
            new byte[2 * 1024 * 1024];

        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            additionalEntries:
            [
                new PackageEntry(
                    "word/media/repetitive.bin",
                    repetitivePayload)
            ]);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWithTooManyEntries_Rejects()
    {
        var additionalEntries =
            new List<PackageEntry>();

        for (int index = 0;
             index < 4_100;
             index++)
        {
            additionalEntries.Add(
                new PackageEntry(
                    $"word/custom/item-{index}.xml",
                    []));
        }

        byte[] content = CreatePackage(
            LegalDocumentFileType.WordOpenXml,
            additionalEntries:
                additionalEntries);

        await AssertInvalidAsync(
            content,
            LegalDocumentFileType.WordOpenXml);
    }

    [Fact]
    public async Task InspectAsync_OpenXmlWrongDeclaredKind_Rejects()
    {
        byte[] docxContent = CreatePackage(
            LegalDocumentFileType.WordOpenXml);

        await AssertInvalidAsync(
            docxContent,
            LegalDocumentFileType.ExcelOpenXml);
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

    private static byte[] CreatePackage(
        LegalDocumentFileType fileType,
        bool includeMainPart = true,
        string? overrideMainContentType = null,
        string? additionalContentType = null,
        string? relationshipTarget = null,
        string? relationshipTargetMode = null,
        string? overrideContentTypesXml = null,
        IReadOnlyCollection<PackageEntry>? additionalEntries = null)
    {
        PackageDescriptor descriptor =
            GetDescriptor(fileType);

        using var output = new MemoryStream();

        using (var archive = new ZipArchive(
            output,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            string contentTypesXml =
                overrideContentTypesXml
                ?? CreateContentTypesXml(
                    descriptor,
                    overrideMainContentType,
                    additionalContentType);

            AddTextEntry(
                archive,
                "[Content_Types].xml",
                contentTypesXml);

            AddTextEntry(
                archive,
                "_rels/.rels",
                CreateRelationshipsXml(
                    relationshipTarget
                        ?? descriptor.MainPartName,
                    relationshipTargetMode));

            if (includeMainPart)
            {
                AddTextEntry(
                    archive,
                    descriptor.MainPartName,
                    descriptor.MainPartXml);
            }

            if (additionalEntries is not null)
            {
                foreach (PackageEntry entry in additionalEntries)
                {
                    AddBinaryEntry(
                        archive,
                        entry.Name,
                        entry.Content);
                }
            }
        }

        return output.ToArray();
    }

    private static string CreateContentTypesXml(
        PackageDescriptor descriptor,
        string? overrideMainContentType,
        string? additionalContentType)
    {
        string extraOverride =
            additionalContentType is null
                ? string.Empty
                : $"""
                     <Override PartName="/custom/macro.xml"
                               ContentType="{additionalContentType}" />
                   """;

        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="{ContentTypesNamespace}">
                  <Default Extension="rels"
                           ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml"
                           ContentType="application/xml" />
                  <Override PartName="/{descriptor.MainPartName}"
                            ContentType="{overrideMainContentType ?? descriptor.MainContentType}" />
                  {extraOverride}
                </Types>
                """;
    }

    private static string CreateRelationshipsXml(
        string target,
        string? targetMode)
    {
        string targetModeAttribute =
            targetMode is null
                ? string.Empty
                : $" TargetMode=\"{targetMode}\"";

        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="{RelationshipsNamespace}">
                  <Relationship Id="rId1"
                                Type="{OfficeDocumentRelationshipType}"
                                Target="{target}"{targetModeAttribute} />
                </Relationships>
                """;
    }

    private static void AddTextEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        AddBinaryEntry(
            archive,
            name,
            Encoding.UTF8.GetBytes(content));
    }

    private static void AddBinaryEntry(
        ZipArchive archive,
        string name,
        byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            name,
            CompressionLevel.SmallestSize);

        using Stream entryStream = entry.Open();
        entryStream.Write(content);
    }

    private static PackageDescriptor GetDescriptor(
        LegalDocumentFileType fileType)
    {
        return fileType switch
        {
            LegalDocumentFileType.WordOpenXml =>
                new PackageDescriptor(
                    "word/document.xml",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:body />
                    </w:document>
                    """),

            LegalDocumentFileType.ExcelOpenXml =>
                new PackageDescriptor(
                    "xl/workbook.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      <sheets />
                    </workbook>
                    """),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(fileType))
        };
    }

    private sealed record PackageDescriptor(
        string MainPartName,
        string MainContentType,
        string MainPartXml);

    private sealed record PackageEntry(
        string Name,
        byte[] Content);
}
