using System.Globalization;
using System.Text;

namespace Enma.Domain.Documents;

public sealed class LegalDocument
{
    public const long MaximumSizeBytes = 25L * 1024L * 1024L;

    private const int MaximumOriginalFileNameUnicodeScalars = 200;
    private const int MaximumOriginalFileNameUtf8Bytes = 255;
    private const int StorageObjectKeyLength = 32;

    public LegalDocument(
        Guid organizationId,
        Guid? clientId,
        Guid? processId,
        string originalFileName,
        string storedObjectKey,
        string contentType,
        long sizeBytes,
        LegalDocumentContentHash contentHashSha256,
        Guid uploadedByMembershipId,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDocumentErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDocumentErrors.ClientIdInvalid,
                nameof(clientId));
        }

        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDocumentErrors.ProcessIdInvalid,
                nameof(processId));
        }

        if (clientId.HasValue && processId.HasValue)
        {
            throw new ArgumentException(
                LegalDocumentErrors.ClassificationInvalid,
                nameof(processId));
        }

        if (contentHashSha256 is null)
        {
            throw new ArgumentNullException(
                nameof(contentHashSha256),
                LegalDocumentErrors.ContentHashRequired);
        }

        if (uploadedByMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDocumentErrors.UploadedByMembershipIdRequired,
                nameof(uploadedByMembershipId));
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                LegalDocumentErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        ClientId = clientId;
        ProcessId = processId;
        OriginalFileName = NormalizeOriginalFileName(
            originalFileName);
        StoredObjectKey = ValidateStoredObjectKey(
            storedObjectKey);
        ContentType = ValidateContentType(contentType);
        SizeBytes = ValidateSizeBytes(sizeBytes);
        ContentHashSha256 = contentHashSha256;
        UploadedByMembershipId = uploadedByMembershipId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid? ClientId { get; private set; }

    public Guid? ProcessId { get; private set; }

    public string OriginalFileName { get; private set; }

    public string StoredObjectKey { get; private set; }

    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    public LegalDocumentContentHash ContentHashSha256 { get; private set; }

    public Guid UploadedByMembershipId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private static string NormalizeOriginalFileName(
        string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new ArgumentException(
                LegalDocumentErrors.OriginalFileNameRequired,
                nameof(originalFileName));
        }

        string normalizedFileName;

        try
        {
            normalizedFileName = originalFileName.Normalize(
                NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                LegalDocumentErrors.OriginalFileNameInvalid,
                nameof(originalFileName));
        }

        if (normalizedFileName is "." or ".."
            || normalizedFileName.EndsWith('.')
            || normalizedFileName.EndsWith(' ')
            || ContainsPortableInvalidFileNameCharacter(
                normalizedFileName)
            || ContainsDisallowedUnicodeCategory(
                normalizedFileName))
        {
            throw new ArgumentException(
                LegalDocumentErrors.OriginalFileNameInvalid,
                nameof(originalFileName));
        }

        int scalarCount =
            normalizedFileName.EnumerateRunes().Count();

        int utf8ByteCount =
            Encoding.UTF8.GetByteCount(normalizedFileName);

        if (scalarCount
                > MaximumOriginalFileNameUnicodeScalars
            || utf8ByteCount
                > MaximumOriginalFileNameUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalFileName),
                LegalDocumentErrors.OriginalFileNameTooLong);
        }

        return normalizedFileName;
    }

    private static bool ContainsPortableInvalidFileNameCharacter(
        string fileName)
    {
        foreach (char character in fileName)
        {
            if (character is
                '/' or '\\' or ':' or '*' or '?' or '"'
                or '<' or '>' or '|')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDisallowedUnicodeCategory(
        string fileName)
    {
        foreach (Rune rune in fileName.EnumerateRunes())
        {
            UnicodeCategory category =
                Rune.GetUnicodeCategory(rune);

            if (category is
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate)
            {
                return true;
            }
        }

        return false;
    }

    private static string ValidateStoredObjectKey(
        string storedObjectKey)
    {
        if (storedObjectKey is null
            || storedObjectKey.Length != StorageObjectKeyLength
            || storedObjectKey.Any(
                character =>
                    character is not (
                        >= '0' and <= '9'
                        or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                LegalDocumentErrors.StoredObjectKeyInvalid,
                nameof(storedObjectKey));
        }

        return storedObjectKey;
    }

    private static string ValidateContentType(string contentType)
    {
        if (contentType is not (
            "application/pdf"
            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            or "image/png"
            or "image/jpeg"))
        {
            throw new ArgumentException(
                LegalDocumentErrors.ContentTypeInvalid,
                nameof(contentType));
        }

        return contentType;
    }

    private static long ValidateSizeBytes(long sizeBytes)
    {
        if (sizeBytes is <= 0 or > MaximumSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                LegalDocumentErrors.SizeBytesInvalid);
        }

        return sizeBytes;
    }
}
