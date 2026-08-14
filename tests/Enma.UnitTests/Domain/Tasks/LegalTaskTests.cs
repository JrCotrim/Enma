using Enma.Domain.Tasks;

namespace Enma.UnitTests.Domain.Tasks;

public sealed class LegalTaskTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "02a3453f-2444-4f8b-ad3e-64229d03696b");

    private static readonly Guid ProcessId = Guid.Parse(
        "6474cd2c-c31c-4780-b568-fb5cbf053238");

    private static readonly Guid AssigneeMembershipId = Guid.Parse(
        "1d297da9-c331-4dd8-9ae1-4d28c64b0fbb");

    private static readonly Guid CreatedByMembershipId = Guid.Parse(
        "44551cc0-90d5-439d-89f3-488d85b2c94b");

    private static readonly DateOnly DueDate = new(2026, 11, 2);

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        14,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithMinimalValues_CreatesPendingUnassignedLegalTask()
    {
        var legalTask = new LegalTask(
            OrganizationId,
            "Prepare Defense",
            null,
            null,
            null,
            null,
            CreatedByMembershipId,
            CreatedAt);

        Assert.NotEqual(Guid.Empty, legalTask.Id);
        Assert.Equal(OrganizationId, legalTask.OrganizationId);
        Assert.Equal("Prepare Defense", legalTask.Title);
        Assert.Null(legalTask.Description);
        Assert.Null(legalTask.DueDate);
        Assert.Null(legalTask.ProcessId);
        Assert.Null(legalTask.AssigneeMembershipId);
        Assert.Equal(CreatedByMembershipId, legalTask.CreatedByMembershipId);
        Assert.Equal(CreatedAt, legalTask.CreatedAt);
        Assert.Null(legalTask.CompletedAt);
    }

    [Fact]
    public void Constructor_WithFullValues_NormalizesAndCreatesLegalTask()
    {
        LegalTask legalTask = CreateLegalTask(
            title: "  Prepare Defense  ",
            description: "  Review documents  ");

        Assert.Equal("Prepare Defense", legalTask.Title);
        Assert.Equal("Review documents", legalTask.Description);
        Assert.Equal(DueDate, legalTask.DueDate);
        Assert.Equal(ProcessId, legalTask.ProcessId);
        Assert.Equal(AssigneeMembershipId, legalTask.AssigneeMembershipId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithUnusableTitle_ThrowsArgumentException(string? title)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateLegalTask(title: title!));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalTaskErrors.TitleRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithTitleAtMaximumLength_AcceptsTitle()
    {
        string title = new('a', 150);

        LegalTask legalTask = CreateLegalTask(title: title);

        Assert.Equal(title, legalTask.Title);
    }

    [Fact]
    public void Constructor_WithTitleBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalTask(title: new string('a', 151)));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalTaskErrors.TitleTooLong, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithEmptyDescription_NormalizesToNull(string? description)
    {
        LegalTask legalTask = CreateLegalTask(description: description);

        Assert.Null(legalTask.Description);
    }

    [Fact]
    public void Constructor_WithDescriptionAtMaximumLength_AcceptsDescription()
    {
        string description = new('a', 2_000);

        LegalTask legalTask = CreateLegalTask(description: description);

        Assert.Equal(description, legalTask.Description);
    }

    [Fact]
    public void Constructor_WithDescriptionBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalTask(description: new string('a', 2_001)));

        Assert.Equal("description", exception.ParamName);
        Assert.Contains(LegalTaskErrors.DescriptionTooLong, exception.Message);
    }

    [Theory]
    [MemberData(nameof(AllowedDueDates))]
    public void Constructor_WithAllowedDueDate_AcceptsDueDate(DateOnly? dueDate)
    {
        var legalTask = new LegalTask(
            OrganizationId,
            "Prepare Defense",
            "Review documents",
            dueDate,
            ProcessId,
            AssigneeMembershipId,
            CreatedByMembershipId,
            CreatedAt);

        Assert.Equal(dueDate, legalTask.DueDate);
    }

    [Fact]
    public void Constructor_WithMinimumDueDate_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalTask(dueDate: DateOnly.MinValue));

        Assert.Equal("dueDate", exception.ParamName);
        Assert.Contains(LegalTaskErrors.DueDateInvalid, exception.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidRequiredIdentifiers))]
    public void Constructor_WithEmptyRequiredIdentifier_ThrowsArgumentException(
        bool emptyOrganizationId,
        bool emptyCreatedByMembershipId,
        string expectedParameterName,
        string expectedError)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalTask(
                emptyOrganizationId ? Guid.Empty : OrganizationId,
                "Prepare Defense",
                null,
                null,
                null,
                null,
                emptyCreatedByMembershipId ? Guid.Empty : CreatedByMembershipId,
                CreatedAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
        Assert.Contains(expectedError, exception.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidOptionalIdentifiers))]
    public void Constructor_WithEmptyOptionalIdentifier_ThrowsArgumentException(
        Guid? processId,
        Guid? assigneeMembershipId,
        string expectedParameterName,
        string expectedError)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateLegalTask(
                processId: processId,
                assigneeMembershipId: assigneeMembershipId));

        Assert.Equal(expectedParameterName, exception.ParamName);
        Assert.Contains(expectedError, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateLegalTask(createdAt: DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(LegalTaskErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void ChangeDetails_WhenPending_UpdatesAndNormalizesAllDetails()
    {
        LegalTask legalTask = CreateLegalTask();
        Guid updatedProcessId = Guid.NewGuid();
        DateOnly updatedDueDate = DueDate.AddDays(7);

        legalTask.ChangeDetails(
            "  Attend Hearing  ",
            "  Bring exhibits  ",
            updatedDueDate,
            updatedProcessId);

        Assert.Equal("Attend Hearing", legalTask.Title);
        Assert.Equal("Bring exhibits", legalTask.Description);
        Assert.Equal(updatedDueDate, legalTask.DueDate);
        Assert.Equal(updatedProcessId, legalTask.ProcessId);
    }

    [Fact]
    public void ChangeDetails_WhenPending_SetsPreviouslyNullOptionalDetails()
    {
        var legalTask = new LegalTask(
            OrganizationId,
            "Prepare Defense",
            null,
            null,
            null,
            null,
            CreatedByMembershipId,
            CreatedAt);

        legalTask.ChangeDetails(
            "Prepare Defense",
            "Review documents",
            DueDate,
            ProcessId);

        Assert.Equal("Review documents", legalTask.Description);
        Assert.Equal(DueDate, legalTask.DueDate);
        Assert.Equal(ProcessId, legalTask.ProcessId);
    }

    [Fact]
    public void ChangeDetails_WhenPending_ClearsOptionalDetails()
    {
        LegalTask legalTask = CreateLegalTask();

        legalTask.ChangeDetails("Updated Title", "   ", null, null);

        Assert.Equal("Updated Title", legalTask.Title);
        Assert.Null(legalTask.Description);
        Assert.Null(legalTask.DueDate);
        Assert.Null(legalTask.ProcessId);
    }

    [Theory]
    [MemberData(nameof(InvalidDetailChanges))]
    public void ChangeDetails_WithInvalidValue_RejectsWithoutMutation(
        string title,
        string? description,
        DateOnly? dueDate,
        Guid? processId,
        Type expectedExceptionType)
    {
        LegalTask legalTask = CreateLegalTask();

        Exception exception = Assert.Throws(
            expectedExceptionType,
            () => legalTask.ChangeDetails(
                title,
                description,
                dueDate,
                processId));

        Assert.NotNull(exception);
        AssertOriginalDetails(legalTask);
    }

    [Fact]
    public void ChangeAssignee_WhenPending_AssignsChangesAndClearsMembership()
    {
        var legalTask = new LegalTask(
            OrganizationId,
            "Prepare Defense",
            "Review documents",
            DueDate,
            ProcessId,
            null,
            CreatedByMembershipId,
            CreatedAt);
        Guid firstAssigneeMembershipId = Guid.NewGuid();
        Guid secondAssigneeMembershipId = Guid.NewGuid();

        legalTask.ChangeAssignee(firstAssigneeMembershipId);
        Assert.Equal(firstAssigneeMembershipId, legalTask.AssigneeMembershipId);

        legalTask.ChangeAssignee(secondAssigneeMembershipId);
        Assert.Equal(secondAssigneeMembershipId, legalTask.AssigneeMembershipId);

        legalTask.ChangeAssignee(null);
        Assert.Null(legalTask.AssigneeMembershipId);
    }

    [Fact]
    public void ChangeAssignee_WithEmptyIdentifier_RejectsWithoutMutation()
    {
        LegalTask legalTask = CreateLegalTask();

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            legalTask.ChangeAssignee(Guid.Empty));

        Assert.Equal("assigneeMembershipId", exception.ParamName);
        Assert.Contains(LegalTaskErrors.AssigneeMembershipIdInvalid, exception.Message);
        Assert.Equal(AssigneeMembershipId, legalTask.AssigneeMembershipId);
    }

    [Fact]
    public void Complete_WhenPending_PreservesExactTimestamp()
    {
        LegalTask legalTask = CreateLegalTask();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);

        legalTask.Complete(completedAt);

        Assert.Equal(completedAt, legalTask.CompletedAt);
    }

    [Fact]
    public void Complete_WhenAlreadyCompleted_PreservesFirstTimestamp()
    {
        LegalTask legalTask = CreateLegalTask();
        DateTimeOffset firstCompletedAt = CreatedAt.AddDays(1);

        legalTask.Complete(firstCompletedAt);
        legalTask.Complete(firstCompletedAt.AddHours(1));

        Assert.Equal(firstCompletedAt, legalTask.CompletedAt);
    }

    [Theory]
    [MemberData(nameof(InvalidCompletionTimestamps))]
    public void Complete_WithInvalidTimestamp_RejectsWithoutMutation(
        DateTimeOffset completedAt,
        string expectedError)
    {
        LegalTask legalTask = CreateLegalTask();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                legalTask.Complete(completedAt));

        Assert.Equal("completedAt", exception.ParamName);
        Assert.Contains(expectedError, exception.Message);
        Assert.Null(legalTask.CompletedAt);
    }

    [Fact]
    public void Reopen_WhenCompleted_ClearsOnlyCompletedAt()
    {
        LegalTask legalTask = CreateLegalTask();
        Guid id = legalTask.Id;
        legalTask.Complete(CreatedAt.AddDays(1));

        legalTask.Reopen();

        Assert.Null(legalTask.CompletedAt);
        Assert.Equal(id, legalTask.Id);
        AssertOriginalDetails(legalTask);
        Assert.Equal(AssigneeMembershipId, legalTask.AssigneeMembershipId);
        Assert.Equal(CreatedByMembershipId, legalTask.CreatedByMembershipId);
        Assert.Equal(CreatedAt, legalTask.CreatedAt);
    }

    [Fact]
    public void Reopen_WhenPending_RemainsPending()
    {
        LegalTask legalTask = CreateLegalTask();

        legalTask.Reopen();
        legalTask.Reopen();

        Assert.Null(legalTask.CompletedAt);
    }

    [Fact]
    public void ChangeDetails_WhenCompleted_RejectsWithConflictWithoutMutation()
    {
        LegalTask legalTask = CreateLegalTask();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);
        legalTask.Complete(completedAt);

        LegalTaskCompletedMutationException exception =
            Assert.Throws<LegalTaskCompletedMutationException>(() =>
                legalTask.ChangeDetails("Updated", null, null, null));

        Assert.Equal(LegalTaskErrors.CompletedTaskCannotChange, exception.Message);
        AssertOriginalDetails(legalTask);
        Assert.Equal(completedAt, legalTask.CompletedAt);
    }

    [Fact]
    public void ChangeAssignee_WhenCompleted_RejectsWithConflictWithoutMutation()
    {
        LegalTask legalTask = CreateLegalTask();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);
        legalTask.Complete(completedAt);

        LegalTaskCompletedMutationException exception =
            Assert.Throws<LegalTaskCompletedMutationException>(() =>
                legalTask.ChangeAssignee(Guid.NewGuid()));

        Assert.Equal(LegalTaskErrors.CompletedTaskCannotChange, exception.Message);
        Assert.Equal(AssigneeMembershipId, legalTask.AssigneeMembershipId);
        Assert.Equal(completedAt, legalTask.CompletedAt);
    }

    [Fact]
    public void Changes_AfterReopen_Succeed()
    {
        LegalTask legalTask = CreateLegalTask();
        Guid updatedAssigneeMembershipId = Guid.NewGuid();
        legalTask.Complete(CreatedAt.AddDays(1));
        legalTask.Reopen();

        legalTask.ChangeDetails("Updated", null, null, null);
        legalTask.ChangeAssignee(updatedAssigneeMembershipId);

        Assert.Equal("Updated", legalTask.Title);
        Assert.Null(legalTask.Description);
        Assert.Null(legalTask.DueDate);
        Assert.Null(legalTask.ProcessId);
        Assert.Equal(updatedAssigneeMembershipId, legalTask.AssigneeMembershipId);
        Assert.Null(legalTask.CompletedAt);
    }

    public static TheoryData<DateOnly?> AllowedDueDates =>
        new()
        {
            null,
            new DateOnly(2020, 1, 1),
            new DateOnly(2026, 8, 14),
            new DateOnly(2030, 12, 31),
            new DateOnly(2028, 2, 29)
        };

    public static TheoryData<bool, bool, string, string> InvalidRequiredIdentifiers =>
        new()
        {
            { true, false, "organizationId", LegalTaskErrors.OrganizationIdRequired },
            {
                false,
                true,
                "createdByMembershipId",
                LegalTaskErrors.CreatedByMembershipIdRequired
            }
        };

    public static TheoryData<Guid?, Guid?, string, string> InvalidOptionalIdentifiers =>
        new()
        {
            { Guid.Empty, AssigneeMembershipId, "processId", LegalTaskErrors.ProcessIdInvalid },
            {
                ProcessId,
                Guid.Empty,
                "assigneeMembershipId",
                LegalTaskErrors.AssigneeMembershipIdInvalid
            }
        };

    public static TheoryData<string, string?, DateOnly?, Guid?, Type>
        InvalidDetailChanges =>
        new()
        {
            { "   ", "Updated", DueDate.AddDays(1), Guid.NewGuid(), typeof(ArgumentException) },
            {
                "Updated",
                new string('a', 2_001),
                DueDate.AddDays(1),
                Guid.NewGuid(),
                typeof(ArgumentOutOfRangeException)
            },
            {
                "Updated",
                "Updated",
                DateOnly.MinValue,
                Guid.NewGuid(),
                typeof(ArgumentOutOfRangeException)
            },
            {
                "Updated",
                "Updated",
                DueDate.AddDays(1),
                Guid.Empty,
                typeof(ArgumentException)
            }
        };

    public static TheoryData<DateTimeOffset, string> InvalidCompletionTimestamps =>
        new()
        {
            { DateTimeOffset.MinValue, LegalTaskErrors.CompletedAtInvalid },
            { CreatedAt.AddTicks(-1), LegalTaskErrors.CompletionBeforeCreation }
        };

    private static LegalTask CreateLegalTask(
        string title = "Prepare Defense",
        string? description = "Review documents",
        DateOnly? dueDate = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null,
        DateTimeOffset? createdAt = null)
    {
        return new LegalTask(
            OrganizationId,
            title,
            description,
            dueDate ?? DueDate,
            processId ?? ProcessId,
            assigneeMembershipId ?? AssigneeMembershipId,
            CreatedByMembershipId,
            createdAt ?? CreatedAt);
    }

    private static void AssertOriginalDetails(LegalTask legalTask)
    {
        Assert.Equal("Prepare Defense", legalTask.Title);
        Assert.Equal("Review documents", legalTask.Description);
        Assert.Equal(DueDate, legalTask.DueDate);
        Assert.Equal(ProcessId, legalTask.ProcessId);
    }
}
