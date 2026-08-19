using Enma.Application.Documents.Inspection;

namespace Enma.UnitTests.Documents;

public sealed class LegalDocumentUploadPolicyTests
{
    [Theory]
    [InlineData("document.pdf", "application/pdf", LegalDocumentFileType.Pdf, ".pdf")]
    [InlineData(
        "contract.docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        LegalDocumentFileType.WordOpenXml,
        ".docx")]
    [InlineData(
        "worksheet.xlsx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        LegalDocumentFileType.ExcelOpenXml,
        ".xlsx")]
    [InlineData("image.png", "image/png", LegalDocumentFileType.Png, ".png")]
    [InlineData("photo.jpg", "image/jpeg", LegalDocumentFileType.Jpeg, ".jpg")]
    [InlineData("photo.jpeg", "image/jpeg", LegalDocumentFileType.Jpeg, ".jpeg")]
    public void Admit_SupportedFile_ReturnsNormalizedAdmission(
        string fileName,
        string contentType,
        LegalDocumentFileType expectedFileType,
        string expectedExtension)
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                fileName,
                contentType,
                128);

        Assert.Equal(fileName, result.OriginalFileName);
        Assert.Equal(expectedExtension, result.Extension);
        Assert.Equal(expectedFileType, result.FileType);
        Assert.Equal(contentType, result.CanonicalContentType);
        Assert.Equal(128, result.ContentLength);
    }

    [Fact]
    public void Admit_DecomposedUnicodeFileName_NormalizesToNfc()
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                "Cafe\u0301.pdf",
                "application/pdf",
                1);

        Assert.Equal("Café.pdf", result.OriginalFileName);
    }

    [Fact]
    public void Admit_UppercaseExtensionAndMime_NormalizesTypeMetadata()
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                "REPORT.PDF",
                "APPLICATION/PDF",
                1);

        Assert.Equal("REPORT.PDF", result.OriginalFileName);
        Assert.Equal(".pdf", result.Extension);
        Assert.Equal("application/pdf", result.CanonicalContentType);
    }

    [Fact]
    public void Admit_ContentTypeWithParameters_UsesMediaTypeOnly()
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                "report.pdf",
                "application/pdf; charset=binary",
                1);

        Assert.Equal("application/pdf", result.CanonicalContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Admit_MissingFileName_Rejects(
        string? fileName)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.MissingFileName,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Theory]
    [InlineData("../report.pdf")]
    [InlineData(@"..\report.pdf")]
    [InlineData(@".\report.pdf")]
    [InlineData(@"C:\report.pdf")]
    [InlineData("/tmp/report.pdf")]
    [InlineData("report:final.pdf")]
    [InlineData("report?.pdf")]
    [InlineData("report*.pdf")]
    [InlineData("report|final.pdf")]
    [InlineData("report<final>.pdf")]
    [InlineData("report\"final.pdf")]
    [InlineData("report.pdf ")]
    [InlineData(".")]
    [InlineData("..")]
    public void Admit_PathLikeOrPortableInvalidFileName_Rejects(
        string fileName)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.InvalidFileName,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Theory]
    [InlineData("report\u0000.pdf")]
    [InlineData("report\r.pdf")]
    [InlineData("report\n.pdf")]
    [InlineData("report\u202Efdp.pdf")]
    [InlineData("report\u200B.pdf")]
    public void Admit_ControlOrFormatCharacter_Rejects(
        string fileName)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.InvalidFileName,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Fact]
    public void Admit_ExactlyMaximumUnicodeScalars_Accepts()
    {
        string fileName =
            new string('a', LegalDocumentUploadPolicy.MaximumFileNameUnicodeScalars - 4)
            + ".pdf";

        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1);

        Assert.Equal(fileName, result.OriginalFileName);
    }

    [Fact]
    public void Admit_TooManyUnicodeScalars_Rejects()
    {
        string fileName =
            new string('a', LegalDocumentUploadPolicy.MaximumFileNameUnicodeScalars - 3)
            + ".pdf";

        AssertRejected(
            LegalDocumentUploadRejectionReason.FileNameTooLong,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Fact]
    public void Admit_ExactlyMaximumUtf8Bytes_Accepts()
    {
        string fileName =
            new string('é', 125)
            + "a.pdf";

        Assert.Equal(
            LegalDocumentUploadPolicy.MaximumFileNameUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(fileName));

        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1);

        Assert.Equal(fileName, result.OriginalFileName);
    }

    [Fact]
    public void Admit_TooManyUtf8Bytes_Rejects()
    {
        string fileName =
            new string('é', 126)
            + ".pdf";

        AssertRejected(
            LegalDocumentUploadRejectionReason.FileNameTooLong,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Theory]
    [InlineData("report")]
    [InlineData(".pdf")]
    [InlineData("report.txt")]
    [InlineData("report.doc")]
    [InlineData("report.xls")]
    [InlineData("report.docm")]
    [InlineData("report.xlsm")]
    [InlineData("report.svg")]
    [InlineData("report.html")]
    [InlineData("report.zip")]
    [InlineData("report.exe")]
    public void Admit_UnsupportedFinalExtension_Rejects(
        string fileName)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.UnsupportedFileType,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                "application/pdf",
                1));
    }

    [Theory]
    [InlineData("invoice.exe.pdf")]
    [InlineData("notes.js.pdf")]
    [InlineData("contract.docm.pdf")]
    [InlineData("sheet.xlsm.pdf")]
    [InlineData("drawing.svg.png")]
    [InlineData("page.html.pdf")]
    [InlineData("archive.zip.pdf")]
    public void Admit_DangerousEmbeddedExtension_Rejects(
        string fileName)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.DangerousEmbeddedExtension,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                GetCompatibleContentType(fileName),
                1));
    }

    [Fact]
    public void Admit_BenignMultipleDots_Accepts()
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                "contract.final.v2.pdf",
                "application/pdf",
                1);

        Assert.Equal("contract.final.v2.pdf", result.OriginalFileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Admit_MissingContentType_Rejects(
        string? contentType)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.MissingContentType,
            () => LegalDocumentUploadPolicy.Admit(
                "report.pdf",
                contentType,
                1));
    }

    [Theory]
    [InlineData("report.pdf", "application/octet-stream")]
    [InlineData("report.pdf", "text/html")]
    [InlineData("photo.jpg", "image/png")]
    [InlineData(
        "contract.docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public void Admit_ContentTypeMismatch_Rejects(
        string fileName,
        string contentType)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.ContentTypeMismatch,
            () => LegalDocumentUploadPolicy.Admit(
                fileName,
                contentType,
                1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Admit_NonPositiveFileLength_Rejects(
        long contentLength)
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.EmptyFile,
            () => LegalDocumentUploadPolicy.Admit(
                "report.pdf",
                "application/pdf",
                contentLength));
    }

    [Fact]
    public void Admit_ExactlyMaximumFileSize_Accepts()
    {
        LegalDocumentUploadAdmission result =
            LegalDocumentUploadPolicy.Admit(
                "report.pdf",
                "application/pdf",
                LegalDocumentUploadPolicy.MaximumFileSizeBytes);

        Assert.Equal(
            LegalDocumentUploadPolicy.MaximumFileSizeBytes,
            result.ContentLength);
    }

    [Fact]
    public void Admit_OverMaximumFileSize_Rejects()
    {
        AssertRejected(
            LegalDocumentUploadRejectionReason.FileTooLarge,
            () => LegalDocumentUploadPolicy.Admit(
                "report.pdf",
                "application/pdf",
                LegalDocumentUploadPolicy.MaximumFileSizeBytes + 1));
    }

    [Fact]
    public void RejectionException_DoesNotEchoSensitiveInput()
    {
        const string sensitiveFileName =
            "client-secret-name.exe.pdf";

        LegalDocumentUploadRejectedException exception =
            Assert.Throws<LegalDocumentUploadRejectedException>(
                () => LegalDocumentUploadPolicy.Admit(
                    sensitiveFileName,
                    "application/pdf",
                    1));

        Assert.DoesNotContain(
            sensitiveFileName,
            exception.Message,
            StringComparison.Ordinal);
    }

    private static void AssertRejected(
        LegalDocumentUploadRejectionReason expectedReason,
        Action action)
    {
        LegalDocumentUploadRejectedException exception =
            Assert.Throws<LegalDocumentUploadRejectedException>(action);

        Assert.Equal(expectedReason, exception.Reason);
    }

    private static string GetCompatibleContentType(string fileName)
    {
        return fileName.EndsWith(
            ".png",
            StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "application/pdf";
    }
}
