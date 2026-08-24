using Enma.Domain.Notifications;

namespace Enma.UnitTests.Domain.Notifications;

public sealed class NotificationTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecipientUserId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid LegalDeadlineId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");
    private static readonly Guid LegalTaskId = Guid.Parse(
        "44444444-4444-4444-4444-444444444444");
    private static readonly Guid CalendarEventId = Guid.Parse(
        "55555555-5555-5555-5555-555555555555");
    private static readonly DateOnly OccurrenceDate = new(2026, 9, 1);
    private static readonly DateTimeOffset OccurrenceAt = new(
        2026,
        9,
        1,
        10,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = OccurrenceAt.AddHours(-1);

    [Theory]
    [MemberData(nameof(ValidShapes))]
    public void Constructor_WithValidShape_PreservesExpectedState(
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt)
    {
        Notification notification = CreateNotification(
            kind,
            legalDeadlineId,
            legalTaskId,
            calendarEventId,
            occurrenceDate,
            occurrenceAt);

        Assert.NotEqual(Guid.Empty, notification.Id);
        Assert.Equal(OrganizationId, notification.OrganizationId);
        Assert.Equal(RecipientUserId, notification.RecipientUserId);
        Assert.Equal(kind, notification.Kind);
        Assert.Equal(legalDeadlineId, notification.LegalDeadlineId);
        Assert.Equal(legalTaskId, notification.LegalTaskId);
        Assert.Equal(calendarEventId, notification.CalendarEventId);
        Assert.Equal(occurrenceDate, notification.OccurrenceDate);
        Assert.Equal(occurrenceAt, notification.OccurrenceAt);
        Assert.Equal(GeneratedAt, notification.GeneratedAt);
        Assert.Null(notification.ReadAt);
    }

    [Theory]
    [InlineData(true, false, "organizationId")]
    [InlineData(false, true, "recipientUserId")]
    public void Constructor_WithEmptyRequiredIdentifier_Throws(
        bool emptyOrganizationId,
        bool emptyRecipientUserId,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new Notification(
                emptyOrganizationId ? Guid.Empty : OrganizationId,
                emptyRecipientUserId ? Guid.Empty : RecipientUserId,
                NotificationKind.LegalDeadlineDueSoon,
                LegalDeadlineId,
                null,
                null,
                OccurrenceDate,
                null,
                GeneratedAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(EmptyOptionalIdentifiers))]
    public void Constructor_WithEmptyOptionalSourceIdentifier_Throws(
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateNotification(
                NotificationKind.LegalDeadlineDueSoon,
                legalDeadlineId,
                legalTaskId,
                calendarEventId,
                OccurrenceDate,
                null));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithUndefinedKind_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateNotification(
                    (NotificationKind)999,
                    LegalDeadlineId,
                    null,
                    null,
                    OccurrenceDate,
                    null));

        Assert.Equal("kind", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidSourceCounts))]
    public void Constructor_WithInvalidSourceCount_Throws(
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateNotification(
                NotificationKind.LegalDeadlineDueSoon,
                legalDeadlineId,
                legalTaskId,
                calendarEventId,
                OccurrenceDate,
                null));

        Assert.Contains(NotificationErrors.SourceInvalid, exception.Message);
    }

    [Theory]
    [MemberData(nameof(MismatchedKindsAndSources))]
    public void Constructor_WithKindSourceMismatch_Throws(
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateNotification(
                kind,
                legalDeadlineId,
                legalTaskId,
                calendarEventId,
                occurrenceDate,
                occurrenceAt));

        Assert.Equal("kind", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidOccurrences))]
    public void Constructor_WithInvalidOccurrenceShape_Throws(
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() =>
            CreateNotification(
                kind,
                legalDeadlineId,
                legalTaskId,
                calendarEventId,
                occurrenceDate,
                occurrenceAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNonUtcInstants_NormalizesToUtcAndPreservesDateOnly()
    {
        DateTimeOffset occurrenceAt = new(
            2026,
            9,
            1,
            7,
            0,
            0,
            TimeSpan.FromHours(-3));
        DateTimeOffset generatedAt = occurrenceAt.AddHours(-1);

        Notification calendarNotification = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            null,
            null,
            CalendarEventId,
            null,
            occurrenceAt,
            generatedAt);
        Notification deadlineNotification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            LegalDeadlineId,
            null,
            null,
            OccurrenceDate,
            null,
            generatedAt);

        Assert.Equal(TimeSpan.Zero, calendarNotification.OccurrenceAt!.Value.Offset);
        Assert.Equal(occurrenceAt.UtcDateTime, calendarNotification.OccurrenceAt.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, calendarNotification.GeneratedAt.Offset);
        Assert.Equal(generatedAt.UtcDateTime, calendarNotification.GeneratedAt.UtcDateTime);
        Assert.Equal(OccurrenceDate, deadlineNotification.OccurrenceDate);
    }

    [Theory]
    [MemberData(nameof(InvalidTimestampValues))]
    public void Constructor_WithInvalidTimestamp_Throws(
        DateTimeOffset? occurrenceAt,
        DateTimeOffset generatedAt,
        string expectedParameterName)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateNotification(
                    NotificationKind.CalendarEventStartingSoon,
                    null,
                    null,
                    CalendarEventId,
                    null,
                    occurrenceAt,
                    generatedAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void MarkAsRead_WithValidTimestamp_NormalizesToUtc()
    {
        Notification notification = CreateDeadlineNotification();
        DateTimeOffset readAt = new(
            2026,
            9,
            1,
            8,
            30,
            0,
            TimeSpan.FromHours(-3));

        notification.MarkAsRead(readAt);

        Assert.Equal(TimeSpan.Zero, notification.ReadAt!.Value.Offset);
        Assert.Equal(readAt.UtcDateTime, notification.ReadAt.Value.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(InvalidReadTimestamps))]
    public void MarkAsRead_WithInvalidTimestamp_Throws(DateTimeOffset readAt)
    {
        Notification notification = CreateDeadlineNotification();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                notification.MarkAsRead(readAt));

        Assert.Equal("readAt", exception.ParamName);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public void MarkAsRead_WhenRepeated_PreservesFirstReadTimestamp()
    {
        Notification notification = CreateDeadlineNotification();
        DateTimeOffset firstReadAt = GeneratedAt.AddMinutes(1);

        notification.MarkAsRead(firstReadAt);
        notification.MarkAsRead(firstReadAt.AddMinutes(1));

        Assert.Equal(firstReadAt, notification.ReadAt);
    }

    [Fact]
    public void MarkAsRead_PreservesIdentityOwnershipSourceAndGenerationMetadata()
    {
        Notification notification = CreateDeadlineNotification();
        Guid id = notification.Id;

        notification.MarkAsRead(GeneratedAt.AddMinutes(1));

        Assert.Equal(id, notification.Id);
        Assert.Equal(OrganizationId, notification.OrganizationId);
        Assert.Equal(RecipientUserId, notification.RecipientUserId);
        Assert.Equal(NotificationKind.LegalDeadlineDueSoon, notification.Kind);
        Assert.Equal(LegalDeadlineId, notification.LegalDeadlineId);
        Assert.Null(notification.LegalTaskId);
        Assert.Null(notification.CalendarEventId);
        Assert.Equal(OccurrenceDate, notification.OccurrenceDate);
        Assert.Null(notification.OccurrenceAt);
        Assert.Equal(GeneratedAt, notification.GeneratedAt);
    }

    public static TheoryData<NotificationKind, Guid?, Guid?, Guid?, DateOnly?, DateTimeOffset?>
        ValidShapes =>
        new()
        {
            {
                NotificationKind.LegalDeadlineDueSoon,
                LegalDeadlineId,
                null,
                null,
                OccurrenceDate,
                null
            },
            {
                NotificationKind.LegalTaskDueSoon,
                null,
                LegalTaskId,
                null,
                OccurrenceDate,
                null
            },
            {
                NotificationKind.CalendarEventStartingSoon,
                null,
                null,
                CalendarEventId,
                null,
                OccurrenceAt
            }
        };

    public static TheoryData<Guid?, Guid?, Guid?, string> EmptyOptionalIdentifiers =>
        new()
        {
            { Guid.Empty, null, null, "legalDeadlineId" },
            { null, Guid.Empty, null, "legalTaskId" },
            { null, null, Guid.Empty, "calendarEventId" }
        };

    public static TheoryData<Guid?, Guid?, Guid?> InvalidSourceCounts =>
        new()
        {
            { null, null, null },
            { LegalDeadlineId, LegalTaskId, null },
            { LegalDeadlineId, LegalTaskId, CalendarEventId }
        };

    public static TheoryData<NotificationKind, Guid?, Guid?, Guid?, DateOnly?, DateTimeOffset?>
        MismatchedKindsAndSources =>
        new()
        {
            {
                NotificationKind.LegalDeadlineDueSoon,
                null,
                LegalTaskId,
                null,
                OccurrenceDate,
                null
            },
            {
                NotificationKind.LegalTaskDueSoon,
                null,
                null,
                CalendarEventId,
                OccurrenceDate,
                null
            },
            {
                NotificationKind.CalendarEventStartingSoon,
                LegalDeadlineId,
                null,
                null,
                null,
                OccurrenceAt
            }
        };

    public static TheoryData<NotificationKind, Guid?, Guid?, Guid?, DateOnly?, DateTimeOffset?, string>
        InvalidOccurrences =>
        new()
        {
            {
                NotificationKind.LegalDeadlineDueSoon,
                LegalDeadlineId,
                null,
                null,
                null,
                null,
                "occurrenceDate"
            },
            {
                NotificationKind.LegalTaskDueSoon,
                null,
                LegalTaskId,
                null,
                OccurrenceDate,
                OccurrenceAt,
                "occurrenceAt"
            },
            {
                NotificationKind.CalendarEventStartingSoon,
                null,
                null,
                CalendarEventId,
                OccurrenceDate,
                OccurrenceAt,
                "occurrenceDate"
            },
            {
                NotificationKind.CalendarEventStartingSoon,
                null,
                null,
                CalendarEventId,
                null,
                null,
                "occurrenceAt"
            },
            {
                NotificationKind.LegalDeadlineDueSoon,
                LegalDeadlineId,
                null,
                null,
                DateOnly.MinValue,
                null,
                "occurrenceDate"
            }
        };

    public static TheoryData<DateTimeOffset?, DateTimeOffset, string>
        InvalidTimestampValues =>
        new()
        {
            { DateTimeOffset.MinValue, GeneratedAt, "occurrenceAt" },
            { OccurrenceAt, DateTimeOffset.MinValue, "generatedAt" }
        };

    public static TheoryData<DateTimeOffset> InvalidReadTimestamps =>
        new()
        {
            DateTimeOffset.MinValue,
            GeneratedAt.AddTicks(-1)
        };

    private static Notification CreateDeadlineNotification()
    {
        return CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            LegalDeadlineId,
            null,
            null,
            OccurrenceDate,
            null);
    }

    private static Notification CreateNotification(
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt,
        DateTimeOffset? generatedAt = null)
    {
        return new Notification(
            OrganizationId,
            RecipientUserId,
            kind,
            legalDeadlineId,
            legalTaskId,
            calendarEventId,
            occurrenceDate,
            occurrenceAt,
            generatedAt ?? GeneratedAt);
    }
}
