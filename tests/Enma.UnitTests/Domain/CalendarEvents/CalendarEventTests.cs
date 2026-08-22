using Enma.Domain.CalendarEvents;

namespace Enma.UnitTests.Domain.CalendarEvents;

public sealed class CalendarEventTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "bf7b6816-1f6b-44ab-a68a-34538b90aefe");

    private static readonly Guid ClientId = Guid.Parse(
        "5931f6ea-935b-49eb-92f6-cef808f16b5c");

    private static readonly Guid ProcessId = Guid.Parse(
        "6432756d-c4d5-4a6d-b4ca-7914cd385ec7");

    private static readonly Guid AssigneeMembershipId = Guid.Parse(
        "2eb2762d-3881-4eb1-90bc-f619d7cc7eda");

    private static readonly Guid CreatedByMembershipId = Guid.Parse(
        "b353ed4a-210a-4207-9cad-55b16fd08956");

    private static readonly DateTimeOffset StartsAt = new(
        2026,
        8,
        24,
        13,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset EndsAt = StartsAt.AddHours(1);

    private static readonly DateTimeOffset CreatedAt = StartsAt.AddDays(-2);

    [Fact]
    public void Constructor_WithGeneralEvent_CreatesTimedCommitment()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        Assert.NotEqual(Guid.Empty, calendarEvent.Id);
        Assert.Equal(OrganizationId, calendarEvent.OrganizationId);
        Assert.Equal("Client Meeting", calendarEvent.Title);
        Assert.Equal("Review the case", calendarEvent.Description);
        Assert.Equal(StartsAt, calendarEvent.StartsAt);
        Assert.Equal(EndsAt, calendarEvent.EndsAt);
        Assert.Equal("Meeting Room 1", calendarEvent.Location);
        Assert.Null(calendarEvent.ClientId);
        Assert.Null(calendarEvent.ProcessId);
        Assert.Null(calendarEvent.AssigneeMembershipId);
        Assert.Equal(
            CreatedByMembershipId,
            calendarEvent.CreatedByMembershipId);
        Assert.Equal(CreatedAt, calendarEvent.CreatedAt);
    }

    [Fact]
    public void Constructor_WithDirectClientEvent_AcceptsClientAssociation()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(clientId: ClientId);

        Assert.Equal(ClientId, calendarEvent.ClientId);
        Assert.Null(calendarEvent.ProcessId);
    }

    [Fact]
    public void Constructor_WithProcessEvent_AcceptsProcessAssociation()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(processId: ProcessId);

        Assert.Null(calendarEvent.ClientId);
        Assert.Equal(ProcessId, calendarEvent.ProcessId);
    }

    [Theory]
    [InlineData(true, false, "organizationId")]
    [InlineData(false, true, "createdByMembershipId")]
    public void Constructor_WithEmptyRequiredIdentifier_ThrowsArgumentException(
        bool emptyOrganizationId,
        bool emptyCreatedByMembershipId,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new CalendarEvent(
                emptyOrganizationId ? Guid.Empty : OrganizationId,
                "Client Meeting",
                null,
                StartsAt,
                EndsAt,
                null,
                null,
                null,
                null,
                emptyCreatedByMembershipId
                    ? Guid.Empty
                    : CreatedByMembershipId,
                CreatedAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(EmptyOptionalIdentifiers))]
    public void Constructor_WithEmptyOptionalIdentifier_ThrowsArgumentException(
        Guid? clientId,
        Guid? processId,
        Guid? assigneeMembershipId,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateCalendarEvent(
                clientId: clientId,
                processId: processId,
                assigneeMembershipId: assigneeMembershipId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithClientAndProcess_RejectsAssociation()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateCalendarEvent(clientId: ClientId, processId: ProcessId));

        Assert.Equal("processId", exception.ParamName);
        Assert.Contains(CalendarEventErrors.AssociationInvalid, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithUnusableTitle_ThrowsArgumentException(string? title)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateCalendarEvent(title: title!));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(CalendarEventErrors.TitleRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithTitle_NormalizesAndEnforcesLimit()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(
            title: $"  {new string('a', 150)}  ");

        Assert.Equal(new string('a', 150), calendarEvent.Title);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCalendarEvent(title: new string('a', 151)));
        Assert.Equal("title", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankDescription_NormalizesToNull(
        string? description)
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(
            description: description);

        Assert.Null(calendarEvent.Description);
    }

    [Fact]
    public void Constructor_WithDescription_NormalizesAndEnforcesLimit()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(
            description: $"  {new string('a', 2_000)}  ");

        Assert.Equal(new string('a', 2_000), calendarEvent.Description);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCalendarEvent(description: new string('a', 2_001)));
        Assert.Equal("description", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankLocation_NormalizesToNull(string? location)
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(location: location);

        Assert.Null(calendarEvent.Location);
    }

    [Fact]
    public void Constructor_WithLocation_NormalizesAndEnforcesLimit()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(
            location: $"  {new string('a', 255)}  ");

        Assert.Equal(new string('a', 255), calendarEvent.Location);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCalendarEvent(location: new string('a', 256)));
        Assert.Equal("location", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidTimeRanges))]
    public void Constructor_WithInvalidTimeRange_ThrowsArgumentOutOfRangeException(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string expectedParameterName)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCalendarEvent(startsAt: startsAt, endsAt: endsAt));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void Constructor_WithCrossDayTimeRange_AcceptsEvent()
    {
        DateTimeOffset startsAt = StartsAt.AddHours(10);
        DateTimeOffset endsAt = startsAt.AddHours(3);

        CalendarEvent calendarEvent = CreateCalendarEvent(
            startsAt: startsAt,
            endsAt: endsAt);

        Assert.NotEqual(startsAt.Date, endsAt.Date);
        Assert.Equal(startsAt, calendarEvent.StartsAt);
        Assert.Equal(endsAt, calendarEvent.EndsAt);
    }

    [Fact]
    public void Constructor_WithNonZeroOffsetTimes_PreservesInstantsInUtc()
    {
        DateTimeOffset startsAt = new(
            2026,
            8,
            24,
            10,
            0,
            0,
            TimeSpan.FromHours(-3));
        DateTimeOffset endsAt = startsAt.AddHours(1);

        CalendarEvent calendarEvent = CreateCalendarEvent(
            startsAt: startsAt,
            endsAt: endsAt);

        Assert.Equal(TimeSpan.Zero, calendarEvent.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, calendarEvent.EndsAt.Offset);
        Assert.Equal(startsAt.UtcDateTime, calendarEvent.StartsAt.UtcDateTime);
        Assert.Equal(endsAt.UtcDateTime, calendarEvent.EndsAt.UtcDateTime);
    }

    [Fact]
    public void Constructor_WithNonZeroOffsetCreatedAt_PreservesInstantInUtc()
    {
        DateTimeOffset createdAt = new(
            2026,
            8,
            22,
            9,
            0,
            0,
            TimeSpan.FromHours(-3));

        CalendarEvent calendarEvent = CreateCalendarEvent(createdAt: createdAt);

        Assert.Equal(TimeSpan.Zero, calendarEvent.CreatedAt.Offset);
        Assert.Equal(createdAt.UtcDateTime, calendarEvent.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateCalendarEvent(createdAt: DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
    }

    [Fact]
    public void ChangeDetails_NormalizesAndClearsOptionalDetails()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        calendarEvent.ChangeDetails("  Hearing  ", "   ", "   ");

        Assert.Equal("Hearing", calendarEvent.Title);
        Assert.Null(calendarEvent.Description);
        Assert.Null(calendarEvent.Location);
    }

    [Theory]
    [InlineData("   ", "Updated", "Updated")]
    [InlineData("Updated", null, null)]
    public void ChangeDetails_WithInvalidValue_RejectsWithoutMutation(
        string title,
        string? description,
        string? location)
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();
        description ??= new string('a', 2_001);
        location ??= new string('a', 256);

        Assert.ThrowsAny<ArgumentException>(() =>
            calendarEvent.ChangeDetails(title, description, location));

        AssertOriginalDetails(calendarEvent);
    }

    [Fact]
    public void ChangeDetails_WithTooLongLocation_RejectsWithoutMutation()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            calendarEvent.ChangeDetails(
                "Updated",
                "Updated",
                new string('a', 256)));

        AssertOriginalDetails(calendarEvent);
    }

    [Fact]
    public void Reschedule_WithValidRange_ChangesOnlyTime()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();
        DateTimeOffset newStartsAt = StartsAt.AddDays(2);
        DateTimeOffset newEndsAt = newStartsAt.AddHours(2);

        calendarEvent.Reschedule(newStartsAt, newEndsAt);

        Assert.Equal(newStartsAt, calendarEvent.StartsAt);
        Assert.Equal(newEndsAt, calendarEvent.EndsAt);
        AssertOriginalIdentity(calendarEvent);
    }

    [Fact]
    public void Reschedule_WithNonZeroOffsets_PreservesInstantsInUtc()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();
        DateTimeOffset newStartsAt = new(
            2026,
            8,
            27,
            14,
            30,
            0,
            TimeSpan.FromHours(-3));
        DateTimeOffset newEndsAt = newStartsAt.AddHours(2);

        calendarEvent.Reschedule(newStartsAt, newEndsAt);

        Assert.Equal(TimeSpan.Zero, calendarEvent.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, calendarEvent.EndsAt.Offset);
        Assert.Equal(
            newStartsAt.UtcDateTime,
            calendarEvent.StartsAt.UtcDateTime);
        Assert.Equal(newEndsAt.UtcDateTime, calendarEvent.EndsAt.UtcDateTime);
    }

    [Theory]
    [MemberData(nameof(InvalidTimeRanges))]
    public void Reschedule_WithInvalidRange_RejectsWithoutMutation(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string _)
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            calendarEvent.Reschedule(startsAt, endsAt));

        Assert.Equal(StartsAt, calendarEvent.StartsAt);
        Assert.Equal(EndsAt, calendarEvent.EndsAt);
    }

    [Fact]
    public void ChangeAssociation_TransitionsBetweenAllAllowedAssociations()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        calendarEvent.ChangeAssociation(ClientId, null);
        Assert.Equal(ClientId, calendarEvent.ClientId);
        Assert.Null(calendarEvent.ProcessId);

        calendarEvent.ChangeAssociation(null, ProcessId);
        Assert.Null(calendarEvent.ClientId);
        Assert.Equal(ProcessId, calendarEvent.ProcessId);

        calendarEvent.ChangeAssociation(null, null);
        Assert.Null(calendarEvent.ClientId);
        Assert.Null(calendarEvent.ProcessId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ChangeAssociation_WithInvalidAssociation_RejectsWithoutMutation(
        bool emptyClientId,
        bool emptyProcessId)
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(clientId: ClientId);
        Guid? clientId = emptyClientId ? Guid.Empty : ClientId;
        Guid? processId = emptyProcessId ? Guid.Empty : ProcessId;

        Assert.Throws<ArgumentException>(() =>
            calendarEvent.ChangeAssociation(clientId, processId));

        Assert.Equal(ClientId, calendarEvent.ClientId);
        Assert.Null(calendarEvent.ProcessId);
    }

    [Fact]
    public void ChangeAssignee_AssignsAndClearsMembership()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();

        calendarEvent.ChangeAssignee(AssigneeMembershipId);
        Assert.Equal(AssigneeMembershipId, calendarEvent.AssigneeMembershipId);

        calendarEvent.ChangeAssignee(null);
        Assert.Null(calendarEvent.AssigneeMembershipId);
    }

    [Fact]
    public void ChangeAssignee_WithEmptyIdentifier_RejectsWithoutMutation()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent(
            assigneeMembershipId: AssigneeMembershipId);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            calendarEvent.ChangeAssignee(Guid.Empty));

        Assert.Equal("assigneeMembershipId", exception.ParamName);
        Assert.Equal(
            AssigneeMembershipId,
            calendarEvent.AssigneeMembershipId);
    }

    [Fact]
    public void Mutations_PreserveImmutableIdentityAndCreationProperties()
    {
        CalendarEvent calendarEvent = CreateCalendarEvent();
        Guid id = calendarEvent.Id;

        calendarEvent.ChangeDetails("Updated", null, null);
        calendarEvent.Reschedule(StartsAt.AddDays(1), EndsAt.AddDays(1));
        calendarEvent.ChangeAssociation(ClientId, null);
        calendarEvent.ChangeAssignee(AssigneeMembershipId);

        Assert.Equal(id, calendarEvent.Id);
        AssertOriginalIdentity(calendarEvent);
    }

    public static TheoryData<Guid?, Guid?, Guid?, string>
        EmptyOptionalIdentifiers =>
        new()
        {
            { Guid.Empty, null, null, "clientId" },
            { null, Guid.Empty, null, "processId" },
            { null, null, Guid.Empty, "assigneeMembershipId" }
        };

    public static TheoryData<DateTimeOffset, DateTimeOffset, string>
        InvalidTimeRanges =>
        new()
        {
            { DateTimeOffset.MinValue, EndsAt, "startsAt" },
            { StartsAt, DateTimeOffset.MinValue, "endsAt" },
            { StartsAt, StartsAt, "endsAt" },
            { StartsAt, StartsAt.AddTicks(-1), "endsAt" }
        };

    private static CalendarEvent CreateCalendarEvent(
        string title = "Client Meeting",
        string? description = "Review the case",
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        string? location = "Meeting Room 1",
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null,
        DateTimeOffset? createdAt = null)
    {
        return new CalendarEvent(
            OrganizationId,
            title,
            description,
            startsAt ?? StartsAt,
            endsAt ?? EndsAt,
            location,
            clientId,
            processId,
            assigneeMembershipId,
            CreatedByMembershipId,
            createdAt ?? CreatedAt);
    }

    private static void AssertOriginalDetails(CalendarEvent calendarEvent)
    {
        Assert.Equal("Client Meeting", calendarEvent.Title);
        Assert.Equal("Review the case", calendarEvent.Description);
        Assert.Equal("Meeting Room 1", calendarEvent.Location);
    }

    private static void AssertOriginalIdentity(CalendarEvent calendarEvent)
    {
        Assert.Equal(OrganizationId, calendarEvent.OrganizationId);
        Assert.Equal(
            CreatedByMembershipId,
            calendarEvent.CreatedByMembershipId);
        Assert.Equal(CreatedAt, calendarEvent.CreatedAt);
    }
}
