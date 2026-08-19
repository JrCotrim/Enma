using System.Buffers;
using System.Globalization;
using System.Text;

namespace Enma.Application.Documents.Inspection;

public static class LegalDocumentUploadPolicy
{
    public const long MaximumFileSizeBytes = 25L * 1024L * 1024L;
    public const int MaximumFileNameUnicodeScalars = 200;
    public const int MaximumFileNameUtf8Bytes = 255;

    private static readonly IReadOnlyDictionary<
        string,
        LegalDocumentFileTypeDescriptor> SupportedFileTypes =
        new Dictionary<string, LegalDocumentFileTypeDescriptor>(
            StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = new(
                LegalDocumentFileType.Pdf,
                "application/pdf"),
            [".docx"] = new(
                LegalDocumentFileType.WordOpenXml,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".xlsx"] = new(
                LegalDocumentFileType.ExcelOpenXml,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".png"] = new(
                LegalDocumentFileType.Png,
                "image/png"),
            [".jpg"] = new(
                LegalDocumentFileType.Jpeg,
                "image/jpeg"),
            [".jpeg"] = new(
                LegalDocumentFileType.Jpeg,
                "image/jpeg")
        };

    private static readonly HashSet<string> DangerousEmbeddedExtensions =
        new(
            [
                "apk",
                "app",
                "asp",
                "aspx",
                "bat",
                "bash",
                "bin",
                "cmd",
                "com",
                "cpl",
                "dll",
                "docm",
                "dotm",
                "exe",
                "fish",
                "hta",
                "htm",
                "html",
                "jar",
                "js",
                "jse",
                "jsp",
                "lnk",
                "msi",
                "msp",
                "php",
                "phtml",
                "ps1",
                "psd1",
                "psm1",
                "scr",
                "sh",
                "svg",
                "sys",
                "vbe",
                "vbs",
                "war",
                "wsf",
                "wsh",
                "xlam",
                "xlsm",
                "xltm",
                "zsh",
                "zip"
            ],
            StringComparer.OrdinalIgnoreCase);

    private static readonly SearchValues<char> PortableInvalidFileNameCharacters =
        SearchValues.Create(
            ['/', '\\', ':', '*', '?', '"', '<', '>', '|']);

    public static LegalDocumentUploadAdmission Admit(
        string? originalFileName,
        string? submittedContentType,
        long contentLength)
    {
        ValidateContentLength(contentLength);

        string normalizedFileName = ValidateAndNormalizeFileName(
            originalFileName);

        string extension = GetSupportedExtension(normalizedFileName);
        ValidateEmbeddedExtensions(normalizedFileName);

        LegalDocumentFileTypeDescriptor descriptor =
            SupportedFileTypes[extension];

        ValidateSubmittedContentType(
            submittedContentType,
            descriptor.CanonicalContentType);

        return new LegalDocumentUploadAdmission(
            normalizedFileName,
            extension.ToLowerInvariant(),
            descriptor.CanonicalContentType,
            descriptor.FileType,
            contentLength);
    }

    private static void ValidateContentLength(long contentLength)
    {
        if (contentLength <= 0)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.EmptyFile);
        }

        if (contentLength > MaximumFileSizeBytes)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.FileTooLarge);
        }
    }

    private static string ValidateAndNormalizeFileName(
        string? originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.MissingFileName);
        }

        string normalizedFileName;

        try
        {
            normalizedFileName = originalFileName.Normalize(
                NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.InvalidFileName);
        }

        if (normalizedFileName is "." or ".."
            || normalizedFileName.EndsWith('.')
            || normalizedFileName.EndsWith(' ')
            || normalizedFileName.AsSpan().IndexOfAny(
                PortableInvalidFileNameCharacters) >= 0
            || ContainsDisallowedUnicode(normalizedFileName))
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.InvalidFileName);
        }

        int scalarCount = normalizedFileName
            .EnumerateRunes()
            .Count();

        if (scalarCount > MaximumFileNameUnicodeScalars
            || Encoding.UTF8.GetByteCount(normalizedFileName)
                > MaximumFileNameUtf8Bytes)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.FileNameTooLong);
        }

        return normalizedFileName;
    }

    private static string GetSupportedExtension(string normalizedFileName)
    {
        int finalDotIndex = normalizedFileName.LastIndexOf('.');

        if (finalDotIndex <= 0
            || finalDotIndex == normalizedFileName.Length - 1)
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.UnsupportedFileType);
        }

        string extension = normalizedFileName[finalDotIndex..];

        if (!SupportedFileTypes.ContainsKey(extension))
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.UnsupportedFileType);
        }

        return extension;
    }

    private static void ValidateEmbeddedExtensions(
        string normalizedFileName)
    {
        string[] segments = normalizedFileName.Split(
            '.',
            StringSplitOptions.None);

        for (int index = 1; index < segments.Length - 1; index++)
        {
            if (DangerousEmbeddedExtensions.Contains(segments[index]))
            {
                throw new LegalDocumentUploadRejectedException(
                    LegalDocumentUploadRejectionReason.DangerousEmbeddedExtension);
            }
        }
    }

    private static void ValidateSubmittedContentType(
        string? submittedContentType,
        string canonicalContentType)
    {
        if (string.IsNullOrWhiteSpace(submittedContentType))
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.MissingContentType);
        }

        int parameterSeparatorIndex = submittedContentType.IndexOf(
            ';',
            StringComparison.Ordinal);
        string mediaType = parameterSeparatorIndex >= 0
            ? submittedContentType[..parameterSeparatorIndex]
            : submittedContentType;

        mediaType = mediaType.Trim();

        if (!string.Equals(
                mediaType,
                canonicalContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LegalDocumentUploadRejectedException(
                LegalDocumentUploadRejectionReason.ContentTypeMismatch);
        }
    }

    private static bool ContainsDisallowedUnicode(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record LegalDocumentFileTypeDescriptor(
        LegalDocumentFileType FileType,
        string CanonicalContentType);
}
