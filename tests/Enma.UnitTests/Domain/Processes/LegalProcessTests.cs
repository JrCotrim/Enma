using Enma.Domain.Processes;

namespace Enma.UnitTests.Domain.Processes;

public sealed class LegalProcessTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "c8e2feb1-2fd9-450d-b6c3-04e7ef44949f");

    private static readonly Guid ClientId = Guid.Parse(
        "06eb3014-6028-4d0f-a5c5-e2a28731c85c");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
        14,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidValues_CreatesLegalProcess()
    {
        var legalProcess = new LegalProcess(
            OrganizationId,
            ClientId,
            "Contract Review",
            CreatedAt);

        Assert.NotEqual(Guid.Empty, legalProcess.Id);
        Assert.Equal(OrganizationId, legalProcess.OrganizationId);
        Assert.Equal(ClientId, legalProcess.ClientId);
        Assert.Equal("Contract Review", legalProcess.Title);
        Assert.Equal(CreatedAt, legalProcess.CreatedAt);
    }

    [Fact]
    public void Constructor_WithEmptyOrganizationId_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalProcess(Guid.Empty, ClientId, "Contract Review", CreatedAt));

        Assert.Equal("organizationId", exception.ParamName);
        Assert.Contains(
            LegalProcessErrors.OrganizationIdRequired,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptyClientId_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalProcess(
                OrganizationId,
                Guid.Empty,
                "Contract Review",
                CreatedAt));

        Assert.Equal("clientId", exception.ParamName);
        Assert.Contains(LegalProcessErrors.ClientIdRequired, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithUnusableTitle_ThrowsArgumentException(
        string? title)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalProcess(OrganizationId, ClientId, title!, CreatedAt));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithSurroundingTitleWhitespace_TrimsTitle()
    {
        var legalProcess = new LegalProcess(
            OrganizationId,
            ClientId,
            "  Contract Review  ",
            CreatedAt);

        Assert.Equal("Contract Review", legalProcess.Title);
    }

    [Fact]
    public void Constructor_WithTitleAtMaximumLength_AcceptsTitle()
    {
        string title = new('a', 150);

        var legalProcess = new LegalProcess(
            OrganizationId,
            ClientId,
            title,
            CreatedAt);

        Assert.Equal(title, legalProcess.Title);
    }

    [Fact]
    public void Constructor_WithTitleBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LegalProcess(
                    OrganizationId,
                    ClientId,
                    new string('a', 151),
                    CreatedAt));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LegalProcess(
                    OrganizationId,
                    ClientId,
                    "Contract Review",
                    DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(LegalProcessErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void ChangeTitle_WithSurroundingWhitespace_TrimsTitle()
    {
        LegalProcess legalProcess = CreateLegalProcess();

        legalProcess.ChangeTitle("  Updated Contract Review  ");

        Assert.Equal("Updated Contract Review", legalProcess.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeTitle_WithUnusableTitle_ThrowsArgumentException(
        string? title)
    {
        LegalProcess legalProcess = CreateLegalProcess();

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            legalProcess.ChangeTitle(title!));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
        Assert.Equal("Contract Review", legalProcess.Title);
    }

    [Fact]
    public void ChangeTitle_WithTitleBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        LegalProcess legalProcess = CreateLegalProcess();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalProcess.ChangeTitle(new string('a', 151)));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
        Assert.Equal("Contract Review", legalProcess.Title);
    }

    [Fact]
    public void ChangeTitle_WithTitleAtMaximumLength_AcceptsTitle()
    {
        LegalProcess legalProcess = CreateLegalProcess();
        string title = new('a', 150);

        legalProcess.ChangeTitle(title);

        Assert.Equal(title, legalProcess.Title);
    }

    [Fact]
    public void ChangeTitle_WithValidTitle_PreservesOwnershipAndCreationDate()
    {
        LegalProcess legalProcess = CreateLegalProcess();
        Guid id = legalProcess.Id;

        legalProcess.ChangeTitle("Updated Contract Review");

        Assert.Equal(id, legalProcess.Id);
        Assert.Equal(OrganizationId, legalProcess.OrganizationId);
        Assert.Equal(ClientId, legalProcess.ClientId);
        Assert.Equal(CreatedAt, legalProcess.CreatedAt);
    }

    private static LegalProcess CreateLegalProcess()
    {
        return new LegalProcess(
            OrganizationId,
            ClientId,
            "Contract Review",
            CreatedAt);
    }
}
