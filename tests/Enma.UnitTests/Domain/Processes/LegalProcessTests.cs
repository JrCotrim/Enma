using Enma.Domain.Auditing;
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
        Assert.Null(legalProcess.ProcessNumber);
        Assert.Null(legalProcess.NormalizedProcessNumber);
        Assert.Equal(LegalProcessStatus.InProgress, legalProcess.Status);
        Assert.Null(legalProcess.CourtOrAuthority);
        Assert.Null(legalProcess.ResponsibleMembershipId);
        Assert.Equal(CreatedAt, legalProcess.CreatedAt);
    }

    [Fact]
    public void Constructor_WithOperationalDetails_NormalizesAndStoresValues()
    {
        Guid responsibleMembershipId = Guid.Parse(
            "d409d293-a61f-4f20-8629-e547da5e4a8a");

        var legalProcess = new LegalProcess(
            OrganizationId,
            ClientId,
            "Contract Review",
            CreatedAt,
            "  ABC-123  ",
            "  Regional Court  ",
            responsibleMembershipId);

        Assert.Equal("ABC-123", legalProcess.ProcessNumber);
        Assert.Equal("ABC-123", legalProcess.NormalizedProcessNumber);
        Assert.Equal("Regional Court", legalProcess.CourtOrAuthority);
        Assert.Equal(
            responsibleMembershipId,
            legalProcess.ResponsibleMembershipId);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Constructor_WithUnusableProcessNumber_StoresNulls(
        string? processNumber)
    {
        LegalProcess legalProcess = CreateLegalProcess(
            processNumber: processNumber);

        Assert.Null(legalProcess.ProcessNumber);
        Assert.Null(legalProcess.NormalizedProcessNumber);
    }

    [Theory]
    [InlineData("0001234-56.2026.8.19.0001")]
    [InlineData("00012345620268190001")]
    [InlineData("0001234 - 56.2026 / 8.19.0001")]
    public void Constructor_WithCnjLikeProcessNumber_NormalizesToTwentyDigits(
        string processNumber)
    {
        LegalProcess legalProcess = CreateLegalProcess(
            processNumber: processNumber);

        Assert.Equal("00012345620268190001", legalProcess.NormalizedProcessNumber);
    }

    [Fact]
    public void Constructor_WithProcessNumber_PreservesTrimmedDisplayValue()
    {
        LegalProcess legalProcess = CreateLegalProcess(
            processNumber: "  Ref-12 / alpha  ");

        Assert.Equal("Ref-12 / alpha", legalProcess.ProcessNumber);
    }

    [Fact]
    public void Constructor_WithFreeFormProcessNumber_NormalizesCaseAndWhitespace()
    {
        LegalProcess legalProcess = CreateLegalProcess(
            processNumber: "  ref-12\t/   alpha  ");

        Assert.Equal("REF-12 / ALPHA", legalProcess.NormalizedProcessNumber);
    }

    [Fact]
    public void Constructor_WithDistinctFreeFormPunctuation_DoesNotCollapseValues()
    {
        LegalProcess punctuated = CreateLegalProcess(
            processNumber: "ABC-123");
        LegalProcess unpunctuated = CreateLegalProcess(
            processNumber: "ABC123");

        Assert.NotEqual(
            punctuated.NormalizedProcessNumber,
            unpunctuated.NormalizedProcessNumber);
    }

    [Fact]
    public void Constructor_WithProcessNumberAtMaximumLength_AcceptsValue()
    {
        string processNumber = new('a', 100);

        LegalProcess legalProcess = CreateLegalProcess(
            processNumber: processNumber);

        Assert.Equal(processNumber, legalProcess.ProcessNumber);
        Assert.Equal(processNumber.ToUpperInvariant(),
            legalProcess.NormalizedProcessNumber);
    }

    [Fact]
    public void Constructor_WithProcessNumberBeyondMaximumLength_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalProcess(processNumber: new string('a', 101)));

        Assert.Equal("processNumber", exception.ParamName);
        Assert.Contains(
            LegalProcessErrors.ProcessNumberTooLong,
            exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Constructor_WithUnusableCourtOrAuthority_StoresNull(
        string? courtOrAuthority)
    {
        LegalProcess legalProcess = CreateLegalProcess(
            courtOrAuthority: courtOrAuthority);

        Assert.Null(legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void Constructor_WithCourtOrAuthority_TrimsValue()
    {
        LegalProcess legalProcess = CreateLegalProcess(
            courtOrAuthority: "  Regional Court  ");

        Assert.Equal("Regional Court", legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void Constructor_WithCourtOrAuthorityAtMaximumLength_AcceptsValue()
    {
        string courtOrAuthority = new('a', 200);

        LegalProcess legalProcess = CreateLegalProcess(
            courtOrAuthority: courtOrAuthority);

        Assert.Equal(courtOrAuthority, legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void Constructor_WithCourtOrAuthorityBeyondMaximumLength_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalProcess(
                    courtOrAuthority: new string('a', 201)));

        Assert.Equal("courtOrAuthority", exception.ParamName);
        Assert.Contains(
            LegalProcessErrors.CourtOrAuthorityTooLong,
            exception.Message);
    }

    [Theory]
    [MemberData(nameof(AllowedStatusTransitions))]
    public void ChangeStatus_WithAllowedTransition_ChangesStatus(
        LegalProcessStatus initial,
        LegalProcessStatus target)
    {
        LegalProcess legalProcess = CreateLegalProcessWithStatus(initial);

        legalProcess.ChangeStatus(target);

        Assert.Equal(target, legalProcess.Status);
    }

    [Fact]
    public void ChangeStatus_FromClosedToSuspended_ThrowsAndPreservesStatus()
    {
        LegalProcess legalProcess = CreateLegalProcessWithStatus(
            LegalProcessStatus.Closed);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => legalProcess.ChangeStatus(LegalProcessStatus.Suspended));

        Assert.Contains(
            LegalProcessErrors.StatusTransitionInvalid,
            exception.Message);
        Assert.Equal(LegalProcessStatus.Closed, legalProcess.Status);
    }

    [Theory]
    [InlineData(LegalProcessStatus.InProgress)]
    [InlineData(LegalProcessStatus.Suspended)]
    [InlineData(LegalProcessStatus.Closed)]
    public void ChangeStatus_WithCurrentStatus_IsNoOp(LegalProcessStatus status)
    {
        LegalProcess legalProcess = CreateLegalProcessWithStatus(status);

        legalProcess.ChangeStatus(status);

        Assert.Equal(status, legalProcess.Status);
    }

    [Fact]
    public void ChangeStatus_WithUndefinedStatus_ThrowsAndPreservesStatus()
    {
        LegalProcess legalProcess = CreateLegalProcess();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalProcess.ChangeStatus((LegalProcessStatus)999));

        Assert.Equal("status", exception.ParamName);
        Assert.Contains(LegalProcessErrors.StatusInvalid, exception.Message);
        Assert.Equal(LegalProcessStatus.InProgress, legalProcess.Status);
    }

    [Fact]
    public void ChangeStatus_ReportsWhetherStatusChanged()
    {
        LegalProcess legalProcess = CreateLegalProcess();

        Assert.True(legalProcess.ChangeStatus(LegalProcessStatus.Closed));
        Assert.False(legalProcess.ChangeStatus(LegalProcessStatus.Closed));
        Assert.True(legalProcess.ChangeStatus(LegalProcessStatus.InProgress));
    }

    [Theory]
    [InlineData(LegalProcessStatus.InProgress, LegalProcessStatus.InProgress, true)]
    [InlineData(LegalProcessStatus.InProgress, LegalProcessStatus.Suspended, true)]
    [InlineData(LegalProcessStatus.Suspended, LegalProcessStatus.Closed, true)]
    [InlineData(LegalProcessStatus.Closed, LegalProcessStatus.InProgress, true)]
    [InlineData(LegalProcessStatus.Closed, LegalProcessStatus.Closed, true)]
    [InlineData(LegalProcessStatus.Closed, LegalProcessStatus.Suspended, false)]
    [InlineData(LegalProcessStatus.InProgress, (LegalProcessStatus)0, false)]
    [InlineData(LegalProcessStatus.Closed, (LegalProcessStatus)999, false)]
    public void CanChangeStatusTo_MatchesTransitionRules(
        LegalProcessStatus initial,
        LegalProcessStatus target,
        bool expected)
    {
        LegalProcess legalProcess = CreateLegalProcessWithStatus(initial);

        Assert.Equal(expected, legalProcess.CanChangeStatusTo(target));
        Assert.Equal(initial, legalProcess.Status);
    }

    [Fact]
    public void ChangeDetails_WithNewValues_NormalizesAndReportsBothFields()
    {
        LegalProcess legalProcess = CreateLegalProcess();

        IReadOnlyList<LegalProcessChangedField> changedFields =
            legalProcess.ChangeDetails(
                "  0001234-56.2026.8.19.0001  ",
                "  1ª Vara Cível  ");

        Assert.Equal(
            [
                LegalProcessChangedField.ProcessNumber,
                LegalProcessChangedField.CourtOrAuthority
            ],
            changedFields);
        Assert.Equal("0001234-56.2026.8.19.0001", legalProcess.ProcessNumber);
        Assert.Equal("00012345620268190001", legalProcess.NormalizedProcessNumber);
        Assert.Equal("1ª Vara Cível", legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_WithSameTrimmedValues_IsNoOp()
    {
        LegalProcess legalProcess = CreateLegalProcess("abc-123", "Court");

        IReadOnlyList<LegalProcessChangedField> changedFields =
            legalProcess.ChangeDetails("  abc-123 ", " Court ");

        Assert.Empty(changedFields);
        Assert.Equal("abc-123", legalProcess.ProcessNumber);
        Assert.Equal("ABC-123", legalProcess.NormalizedProcessNumber);
        Assert.Equal("Court", legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_WithRepresentationOnlyChange_ReportsProcessNumber()
    {
        LegalProcess legalProcess = CreateLegalProcess(
            "0001234-56.2026.8.19.0001",
            "Court");

        IReadOnlyList<LegalProcessChangedField> changedFields =
            legalProcess.ChangeDetails("00012345620268190001", "Court");

        Assert.Equal([LegalProcessChangedField.ProcessNumber], changedFields);
        Assert.Equal("00012345620268190001", legalProcess.ProcessNumber);
        Assert.Equal("00012345620268190001", legalProcess.NormalizedProcessNumber);
    }

    [Fact]
    public void ChangeDetails_WithOnlyCourtChange_ReportsCourtOrAuthority()
    {
        LegalProcess legalProcess = CreateLegalProcess("ABC 1", "Old Court");

        IReadOnlyList<LegalProcessChangedField> changedFields =
            legalProcess.ChangeDetails("ABC 1", "New Court");

        Assert.Equal([LegalProcessChangedField.CourtOrAuthority], changedFields);
        Assert.Equal("ABC 1", legalProcess.ProcessNumber);
        Assert.Equal("New Court", legalProcess.CourtOrAuthority);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\t")]
    public void ChangeDetails_WithUnusableValues_ClearsFields(
        string? processNumber,
        string? courtOrAuthority)
    {
        LegalProcess legalProcess = CreateLegalProcess("ABC-123", "Court");

        IReadOnlyList<LegalProcessChangedField> changedFields =
            legalProcess.ChangeDetails(processNumber, courtOrAuthority);

        Assert.Equal(
            [
                LegalProcessChangedField.ProcessNumber,
                LegalProcessChangedField.CourtOrAuthority
            ],
            changedFields);
        Assert.Null(legalProcess.ProcessNumber);
        Assert.Null(legalProcess.NormalizedProcessNumber);
        Assert.Null(legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_AtMaximumLengths_AcceptsValues()
    {
        LegalProcess legalProcess = CreateLegalProcess();
        string processNumber = new('A', 100);
        string courtOrAuthority = new('B', 200);

        legalProcess.ChangeDetails(processNumber, courtOrAuthority);

        Assert.Equal(processNumber, legalProcess.ProcessNumber);
        Assert.Equal(courtOrAuthority, legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_WithProcessNumberBeyondMaximum_ThrowsAndPreservesState()
    {
        LegalProcess legalProcess = CreateLegalProcess("ABC-123", "Court");

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalProcess.ChangeDetails(new string('A', 101), "Other Court"));

        Assert.Equal("processNumber", exception.ParamName);
        Assert.Equal("ABC-123", legalProcess.ProcessNumber);
        Assert.Equal("ABC-123", legalProcess.NormalizedProcessNumber);
        Assert.Equal("Court", legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_WithCourtBeyondMaximum_ThrowsAndPreservesState()
    {
        LegalProcess legalProcess = CreateLegalProcess("ABC-123", "Court");

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalProcess.ChangeDetails("XYZ-9", new string('B', 201)));

        Assert.Equal("courtOrAuthority", exception.ParamName);
        Assert.Equal("ABC-123", legalProcess.ProcessNumber);
        Assert.Equal("Court", legalProcess.CourtOrAuthority);
    }

    [Fact]
    public void ChangeDetails_PreservesIdentityTitleStatusAndResponsible()
    {
        Guid responsibleMembershipId = Guid.NewGuid();
        LegalProcess legalProcess = CreateLegalProcess();
        legalProcess.ChangeResponsible(responsibleMembershipId);
        legalProcess.ChangeStatus(LegalProcessStatus.Suspended);
        Guid id = legalProcess.Id;

        legalProcess.ChangeDetails("ABC-123", "Court");

        Assert.Equal(id, legalProcess.Id);
        Assert.Equal(OrganizationId, legalProcess.OrganizationId);
        Assert.Equal(ClientId, legalProcess.ClientId);
        Assert.Equal("Contract Review", legalProcess.Title);
        Assert.Equal(CreatedAt, legalProcess.CreatedAt);
        Assert.Equal(LegalProcessStatus.Suspended, legalProcess.Status);
        Assert.Equal(responsibleMembershipId, legalProcess.ResponsibleMembershipId);
    }

    [Fact]
    public void ChangeResponsible_SetsClearsAndReportsChanges()
    {
        Guid firstMembershipId = Guid.NewGuid();
        Guid secondMembershipId = Guid.NewGuid();
        LegalProcess legalProcess = CreateLegalProcess();

        Assert.False(legalProcess.ChangeResponsible(null));
        Assert.True(legalProcess.ChangeResponsible(firstMembershipId));
        Assert.Equal(firstMembershipId, legalProcess.ResponsibleMembershipId);
        Assert.False(legalProcess.ChangeResponsible(firstMembershipId));
        Assert.True(legalProcess.ChangeResponsible(secondMembershipId));
        Assert.Equal(secondMembershipId, legalProcess.ResponsibleMembershipId);
        Assert.True(legalProcess.ChangeResponsible(null));
        Assert.Null(legalProcess.ResponsibleMembershipId);
        Assert.Equal(LegalProcessStatus.InProgress, legalProcess.Status);
    }

    [Fact]
    public void ChangeResponsible_WithEmptyIdentifier_ThrowsAndPreservesState()
    {
        Guid responsibleMembershipId = Guid.NewGuid();
        LegalProcess legalProcess = CreateLegalProcess();
        legalProcess.ChangeResponsible(responsibleMembershipId);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            legalProcess.ChangeResponsible(Guid.Empty));

        Assert.Equal("responsibleMembershipId", exception.ParamName);
        Assert.Equal(responsibleMembershipId, legalProcess.ResponsibleMembershipId);
    }

    [Fact]
    public void Constructor_WithEmptyResponsibleMembershipId_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalProcess(
                OrganizationId,
                ClientId,
                "Contract Review",
                CreatedAt,
                responsibleMembershipId: Guid.Empty));

        Assert.Equal("responsibleMembershipId", exception.ParamName);
        Assert.Contains(
            LegalProcessErrors.ResponsibleMembershipIdInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData("0001234-56.2026.8.19.0001", "00012345620268190001")]
    [InlineData("  0001234-56.2026.8.19.0001  ", "00012345620268190001")]
    [InlineData("0001234-56", "000123456")]
    [InlineData("00012345620268190001", "00012345620268190001")]
    [InlineData("12/34 56", "123456")]
    [InlineData("7", "7")]
    public void ToProcessNumberDigitsSearchTerm_WithDigitsAndSeparators_ReturnsDigits(
        string searchTerm,
        string expected)
    {
        Assert.Equal(
            expected,
            LegalProcess.ToProcessNumberDigitsSearchTerm(searchTerm));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".-/")]
    [InlineData("ABC-123")]
    [InlineData("0001234-56 Vara")]
    [InlineData("123%")]
    [InlineData("١٢٣")]
    public void ToProcessNumberDigitsSearchTerm_WithoutOnlyAsciiDigits_ReturnsNull(
        string? searchTerm)
    {
        Assert.Null(LegalProcess.ToProcessNumberDigitsSearchTerm(searchTerm));
    }

    public static TheoryData<LegalProcessStatus, LegalProcessStatus>
        AllowedStatusTransitions => new()
        {
            { LegalProcessStatus.InProgress, LegalProcessStatus.Suspended },
            { LegalProcessStatus.InProgress, LegalProcessStatus.Closed },
            { LegalProcessStatus.Suspended, LegalProcessStatus.InProgress },
            { LegalProcessStatus.Suspended, LegalProcessStatus.Closed },
            { LegalProcessStatus.Closed, LegalProcessStatus.InProgress }
        };

    private static LegalProcess CreateLegalProcess(
        string? processNumber = null,
        string? courtOrAuthority = null)
    {
        return new LegalProcess(
            OrganizationId,
            ClientId,
            "Contract Review",
            CreatedAt,
            processNumber,
            courtOrAuthority);
    }

    private static LegalProcess CreateLegalProcessWithStatus(
        LegalProcessStatus status)
    {
        LegalProcess legalProcess = CreateLegalProcess();

        if (status != LegalProcessStatus.InProgress)
        {
            legalProcess.ChangeStatus(status);
        }

        return legalProcess;
    }
}
