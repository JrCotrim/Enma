using System.Text;
using Enma.Domain.Documents;

namespace Enma.UnitTests.Domain.Documents;

public sealed class LegalDocumentTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("70e9ae2d-0f37-42ad-932f-43cb4651ced7");
    private static readonly Guid ClientId =
        Guid.Parse("bcda11c9-f86a-4e1f-a956-d7787d08de44");
    private static readonly Guid ProcessId =
        Guid.Parse("cbb3135e-3bf6-4c40-a52e-02f4ddce731b");
    private static readonly Guid MembershipId =
        Guid.Parse("57eb896e-5198-4c58-b64a-baf645f91364");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
        15,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithGeneralDocument_StoresImmutableMetadata()
    {
        LegalDocument document = CreateDocument();

        Assert.NotEqual(Guid.Empty, document.Id);
        Assert.Equal(OrganizationId, document.OrganizationId);
        Assert.Null(document.ClientId);
        Assert.Null(document.ProcessId);
        Assert.Equal("contract.pdf", document.OriginalFileName);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            document.StoredObjectKey);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(1234, document.SizeBytes);
        Assert.Equal(
            CreateHash().ToArray(),
            document.ContentHashSha256.ToArray());
        Assert.Equal(
            MembershipId,
            document.UploadedByMembershipId);
        Assert.Equal(CreatedAt, document.CreatedAt);
    }

    [Fact]
    public void Constructor_WithClientClassification_StoresClientOnly()
    {
        LegalDocument document = CreateDocument(
            clientId: ClientId);

        Assert.Equal(ClientId, document.ClientId);
        Assert.Null(document.ProcessId);
    }

    [Fact]
    public void Constructor_WithProcessClassification_StoresProcessOnly()
    {
        LegalDocument document = CreateDocument(
            processId: ProcessId);

        Assert.Null(document.ClientId);
        Assert.Equal(ProcessId, document.ProcessId);
    }

    [Fact]
    public void Constructor_WithClientAndProcess_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                clientId: ClientId,
                processId: ProcessId));

        Assert.Equal("processId", exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.ClassificationInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData("organizationId")]
    [InlineData("uploadedByMembershipId")]
    public void Constructor_WithRequiredEmptyIdentifier_ThrowsArgumentException(
        string parameterName)
    {
        Guid organizationId = parameterName == "organizationId"
            ? Guid.Empty
            : OrganizationId;
        Guid membershipId =
            parameterName == "uploadedByMembershipId"
                ? Guid.Empty
                : MembershipId;

        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                organizationId: organizationId,
                uploadedByMembershipId: membershipId));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithEmptyClientId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(clientId: Guid.Empty));

        Assert.Equal("clientId", exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.ClientIdInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptyProcessId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(processId: Guid.Empty));

        Assert.Equal("processId", exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.ProcessIdInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithMissingFileName_ThrowsArgumentException(
        string fileName)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                originalFileName: fileName));

        Assert.Equal(
            "originalFileName",
            exception.ParamName);
    }

    [Theory]
    [InlineData("../contract.pdf")]
    [InlineData("..\\contract.pdf")]
    [InlineData("C:\\contract.pdf")]
    [InlineData("contract?.pdf")]
    [InlineData("contract.pdf ")]
    [InlineData("contract.")]
    [InlineData(".")]
    [InlineData("..")]
    public void Constructor_WithUnsafeFileName_ThrowsArgumentException(
        string fileName)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                originalFileName: fileName));

        Assert.Equal(
            "originalFileName",
            exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.OriginalFileNameInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithNonNfcFileName_NormalizesToNfc()
    {
        const string decomposed =
            "Cafe\u0301.pdf";

        LegalDocument document = CreateDocument(
            originalFileName: decomposed);

        Assert.Equal("Café.pdf", document.OriginalFileName);
        Assert.True(
            document.OriginalFileName.IsNormalized(
                NormalizationForm.FormC));
    }

    [Fact]
    public void Constructor_WithMoreThanTwoHundredUnicodeScalars_Throws()
    {
        string fileName =
            new string('a', 197) + ".pdf";

        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateDocument(
                    originalFileName: fileName));

        Assert.Equal(
            "originalFileName",
            exception.ParamName);
    }

    [Fact]
    public void Constructor_WithMoreThanTwoHundredFiftyFiveUtf8Bytes_Throws()
    {
        string fileName =
            new string('é', 126) + ".pdf";

        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateDocument(
                    originalFileName: fileName));

        Assert.Equal(
            "originalFileName",
            exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void Constructor_WithInvalidStorageObjectKey_Throws(
        string? objectKey)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                storedObjectKey: objectKey!));

        Assert.Equal(
            "storedObjectKey",
            exception.ParamName);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("APPLICATION/PDF")]
    [InlineData("application/pdf; charset=binary")]
    [InlineData("")]
    public void Constructor_WithNonCanonicalContentType_Throws(
        string contentType)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateDocument(
                contentType: contentType));

        Assert.Equal("contentType", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(26214401)]
    public void Constructor_WithInvalidSize_ThrowsArgumentOutOfRangeException(
        long sizeBytes)
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateDocument(
                    sizeBytes: sizeBytes));

        Assert.Equal("sizeBytes", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithMaximumSize_AcceptsDocument()
    {
        LegalDocument document = CreateDocument(
            sizeBytes: LegalDocument.MaximumSizeBytes);

        Assert.Equal(
            LegalDocument.MaximumSizeBytes,
            document.SizeBytes);
    }

    [Fact]
    public void Constructor_WithNullHash_ThrowsArgumentNullException()
    {
        var exception =
            Assert.Throws<ArgumentNullException>(
                () => new LegalDocument(
                    OrganizationId,
                    null,
                    null,
                    "contract.pdf",
                    "0123456789abcdef0123456789abcdef",
                    "application/pdf",
                    1234,
                    null!,
                    MembershipId,
                    CreatedAt));

        Assert.Equal(
            "contentHashSha256",
            exception.ParamName);
    }

    [Fact]
    public void Constructor_WithMinimumTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateDocument(
                    createdAt: DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
    }

    private static LegalDocument CreateDocument(
        Guid? clientId = null,
        Guid? processId = null,
        string originalFileName = "contract.pdf",
        string storedObjectKey =
            "0123456789abcdef0123456789abcdef",
        string contentType = "application/pdf",
        long sizeBytes = 1234,
        LegalDocumentContentHash? contentHashSha256 = null,
        Guid? organizationId = null,
        Guid? uploadedByMembershipId = null,
        DateTimeOffset? createdAt = null)
    {
        return new LegalDocument(
            organizationId ?? OrganizationId,
            clientId,
            processId,
            originalFileName,
            storedObjectKey,
            contentType,
            sizeBytes,
            contentHashSha256 ?? CreateHash(),
            uploadedByMembershipId ?? MembershipId,
            createdAt ?? CreatedAt);
    }

    private static LegalDocumentContentHash CreateHash()
    {
        return new LegalDocumentContentHash(
            Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray());
    }
}
