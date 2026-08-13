using Enma.Domain.Deadlines;

namespace Enma.UnitTests.Domain.Deadlines;

public sealed class LegalDeadlineTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "2de505a9-f441-4a11-8979-69770a79a011");

    private static readonly Guid ProcessId = Guid.Parse(
        "b2683a8f-18f4-4606-927c-200d91c364cd");

    private static readonly DateOnly DueDate = new(2026, 11, 2);

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        14,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidValues_CreatesPendingLegalDeadline()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        Assert.NotEqual(Guid.Empty, legalDeadline.Id);
        Assert.Equal(OrganizationId, legalDeadline.OrganizationId);
        Assert.Equal(ProcessId, legalDeadline.ProcessId);
        Assert.Equal("File Appellate Brief", legalDeadline.Title);
        Assert.Equal(DueDate, legalDeadline.DueDate);
        Assert.Equal(CreatedAt, legalDeadline.CreatedAt);
        Assert.Null(legalDeadline.CompletedAt);
    }

    [Fact]
    public void Constructor_WithEmptyOrganizationId_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalDeadline(
                Guid.Empty,
                ProcessId,
                "File Appellate Brief",
                DueDate,
                CreatedAt));

        Assert.Equal("organizationId", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.OrganizationIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptyProcessId_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalDeadline(
                OrganizationId,
                Guid.Empty,
                "File Appellate Brief",
                DueDate,
                CreatedAt));

        Assert.Equal("processId", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.ProcessIdRequired, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithUnusableTitle_ThrowsArgumentException(string? title)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalDeadline(
                OrganizationId,
                ProcessId,
                title!,
                DueDate,
                CreatedAt));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.TitleRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithSurroundingTitleWhitespace_TrimsTitle()
    {
        var legalDeadline = new LegalDeadline(
            OrganizationId,
            ProcessId,
            "  File Appellate Brief  ",
            DueDate,
            CreatedAt);

        Assert.Equal("File Appellate Brief", legalDeadline.Title);
    }

    [Fact]
    public void Constructor_WithTitleAtMaximumLength_AcceptsTitle()
    {
        string title = new('a', 150);

        var legalDeadline = new LegalDeadline(
            OrganizationId,
            ProcessId,
            title,
            DueDate,
            CreatedAt);

        Assert.Equal(title, legalDeadline.Title);
    }

    [Fact]
    public void Constructor_WithTitleBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LegalDeadline(
                    OrganizationId,
                    ProcessId,
                    new string('a', 151),
                    DueDate,
                    CreatedAt));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.TitleTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumDueDate_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LegalDeadline(
                    OrganizationId,
                    ProcessId,
                    "File Appellate Brief",
                    DateOnly.MinValue,
                    CreatedAt));

        Assert.Equal("dueDate", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.DueDateInvalid, exception.Message);
    }

    [Theory]
    [MemberData(nameof(AllowedDueDates))]
    public void Constructor_WithPastTodayOrFutureDueDate_AcceptsDueDate(
        DateOnly dueDate)
    {
        var legalDeadline = new LegalDeadline(
            OrganizationId,
            ProcessId,
            "File Appellate Brief",
            dueDate,
            CreatedAt);

        Assert.Equal(dueDate, legalDeadline.DueDate);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LegalDeadline(
                    OrganizationId,
                    ProcessId,
                    "File Appellate Brief",
                    DueDate,
                    DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void Complete_WhenPending_SetsCompletedAt()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);

        legalDeadline.Complete(completedAt);

        Assert.Equal(completedAt, legalDeadline.CompletedAt);
    }

    [Fact]
    public void Complete_WhenAlreadyCompleted_PreservesFirstCompletionTimestamp()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        DateTimeOffset firstCompletedAt = CreatedAt.AddDays(1);

        legalDeadline.Complete(firstCompletedAt);
        legalDeadline.Complete(firstCompletedAt.AddHours(1));

        Assert.Equal(firstCompletedAt, legalDeadline.CompletedAt);
    }

    [Fact]
    public void Complete_WithMinimumTimestamp_ThrowsArgumentOutOfRangeException()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalDeadline.Complete(DateTimeOffset.MinValue));

        Assert.Equal("completedAt", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.CompletedAtInvalid, exception.Message);
        Assert.Null(legalDeadline.CompletedAt);
    }

    [Fact]
    public void Complete_WithTimestampBeforeCreation_ThrowsArgumentOutOfRangeException()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalDeadline.Complete(CreatedAt.AddTicks(-1)));

        Assert.Equal("completedAt", exception.ParamName);
        Assert.Contains(
            LegalDeadlineErrors.CompletionBeforeCreation,
            exception.Message);
        Assert.Null(legalDeadline.CompletedAt);
    }

    [Fact]
    public void Complete_WithCreationTimestamp_SetsCompletedAt()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        legalDeadline.Complete(CreatedAt);

        Assert.Equal(CreatedAt, legalDeadline.CompletedAt);
    }

    [Fact]
    public void Complete_WithValidTimestamp_PreservesDetailsAndOwnership()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        Guid id = legalDeadline.Id;

        legalDeadline.Complete(CreatedAt.AddDays(1));

        Assert.Equal(id, legalDeadline.Id);
        Assert.Equal(OrganizationId, legalDeadline.OrganizationId);
        Assert.Equal(ProcessId, legalDeadline.ProcessId);
        Assert.Equal("File Appellate Brief", legalDeadline.Title);
        Assert.Equal(DueDate, legalDeadline.DueDate);
        Assert.Equal(CreatedAt, legalDeadline.CreatedAt);
    }

    [Fact]
    public void Reopen_WhenCompleted_ClearsCompletedAtAndPreservesOtherFields()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        Guid id = legalDeadline.Id;
        legalDeadline.Complete(CreatedAt.AddDays(1));

        legalDeadline.Reopen();

        Assert.Null(legalDeadline.CompletedAt);
        Assert.Equal(id, legalDeadline.Id);
        Assert.Equal(OrganizationId, legalDeadline.OrganizationId);
        Assert.Equal(ProcessId, legalDeadline.ProcessId);
        Assert.Equal("File Appellate Brief", legalDeadline.Title);
        Assert.Equal(DueDate, legalDeadline.DueDate);
        Assert.Equal(CreatedAt, legalDeadline.CreatedAt);
    }

    [Fact]
    public void Reopen_WhenPending_RemainsPending()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        legalDeadline.Reopen();
        legalDeadline.Reopen();

        Assert.Null(legalDeadline.CompletedAt);
    }

    [Fact]
    public void ChangeDetails_WhenPending_UpdatesNormalizedTitleAndDueDate()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        DateOnly updatedDueDate = DueDate.AddDays(7);

        legalDeadline.ChangeDetails("  Attend Hearing  ", updatedDueDate);

        Assert.Equal("Attend Hearing", legalDeadline.Title);
        Assert.Equal(updatedDueDate, legalDeadline.DueDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeDetails_WithUnusableTitle_RejectsWithoutMutation(string? title)
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            legalDeadline.ChangeDetails(title!, DueDate.AddDays(1)));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.TitleRequired, exception.Message);
        AssertOriginalDetails(legalDeadline);
    }

    [Fact]
    public void ChangeDetails_WithTitleBeyondMaximumLength_RejectsWithoutMutation()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalDeadline.ChangeDetails(new string('a', 151), DueDate.AddDays(1)));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.TitleTooLong, exception.Message);
        AssertOriginalDetails(legalDeadline);
    }

    [Fact]
    public void ChangeDetails_WithTitleAtMaximumLength_UpdatesDetails()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        string title = new('a', 150);
        DateOnly updatedDueDate = DueDate.AddDays(1);

        legalDeadline.ChangeDetails(title, updatedDueDate);

        Assert.Equal(title, legalDeadline.Title);
        Assert.Equal(updatedDueDate, legalDeadline.DueDate);
    }

    [Fact]
    public void ChangeDetails_WithMinimumDueDate_RejectsWithoutPartialMutation()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalDeadline.ChangeDetails("Updated Title", DateOnly.MinValue));

        Assert.Equal("dueDate", exception.ParamName);
        Assert.Contains(LegalDeadlineErrors.DueDateInvalid, exception.Message);
        AssertOriginalDetails(legalDeadline);
    }

    [Fact]
    public void ChangeDetails_WhenCompleted_RejectsWithoutMutation()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);
        legalDeadline.Complete(completedAt);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            legalDeadline.ChangeDetails("Updated Title", DueDate.AddDays(1)));

        Assert.Contains(
            LegalDeadlineErrors.CompletedDeadlineDetailsCannotChange,
            exception.Message);
        AssertOriginalDetails(legalDeadline);
        Assert.Equal(completedAt, legalDeadline.CompletedAt);
    }

    [Fact]
    public void ChangeDetails_AfterReopen_UpdatesDetails()
    {
        LegalDeadline legalDeadline = CreateLegalDeadline();
        legalDeadline.Complete(CreatedAt.AddDays(1));
        legalDeadline.Reopen();

        legalDeadline.ChangeDetails("Updated Title", DueDate.AddDays(1));

        Assert.Equal("Updated Title", legalDeadline.Title);
        Assert.Equal(DueDate.AddDays(1), legalDeadline.DueDate);
        Assert.Null(legalDeadline.CompletedAt);
    }

    public static TheoryData<DateOnly> AllowedDueDates =>
        new()
        {
            new DateOnly(2020, 1, 1),
            new DateOnly(2026, 8, 13),
            new DateOnly(2030, 12, 31)
        };

    private static LegalDeadline CreateLegalDeadline()
    {
        return new LegalDeadline(
            OrganizationId,
            ProcessId,
            "File Appellate Brief",
            DueDate,
            CreatedAt);
    }

    private static void AssertOriginalDetails(LegalDeadline legalDeadline)
    {
        Assert.Equal("File Appellate Brief", legalDeadline.Title);
        Assert.Equal(DueDate, legalDeadline.DueDate);
    }
}
