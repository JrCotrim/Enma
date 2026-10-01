using Enma.Application.Time;

namespace Enma.UnitTests.Application.Time;

public sealed class OperationalCalendarTests
{
    private static readonly TimeZoneInfo SaoPaulo =
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    [Theory]
    [InlineData("2026-09-29T23:59:00Z", "2026-09-29")] // 20:59 BRT
    [InlineData("2026-09-30T00:00:00Z", "2026-09-29")] // 21:00 BRT
    [InlineData("2026-09-30T02:59:59.9999999Z", "2026-09-29")] // 23:59 BRT
    [InlineData("2026-09-30T03:00:00Z", "2026-09-30")] // 00:00 BRT
    public void GetToday_UsesOperationalCivilDateAroundUtcAndLocalMidnight(
        string utcNow,
        string expectedToday)
    {
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.Parse(utcNow),
            SaoPaulo);

        Assert.Equal(DateOnly.Parse(expectedToday), calendar.GetToday());
    }

    [Fact]
    public void GetToday_IgnoresOffsetCarriedByTimeProvider()
    {
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.Parse("2026-09-30T09:30:00+09:00"),
            SaoPaulo);

        Assert.Equal(new DateOnly(2026, 9, 29), calendar.GetToday());
        Assert.Equal(TimeSpan.Zero, calendar.GetUtcNow().Offset);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-30T00:30:00Z"),
            calendar.GetUtcNow());
    }

    [Fact]
    public void GetToday_ReadsClockOnce()
    {
        var clock = new RecordingTimeProvider(
            DateTimeOffset.Parse("2026-09-30T01:00:00Z"));
        var calendar = new OperationalCalendar(clock, SaoPaulo);

        DateOnly today = calendar.GetToday();

        Assert.Equal(new DateOnly(2026, 9, 29), today);
        Assert.Equal(1, clock.CallCount);
    }

    [Fact]
    public void GetStartOfDayUtc_RegularDate_ReturnsLocalMidnightInUtc()
    {
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.UnixEpoch,
            SaoPaulo);

        DateTimeOffset start = calendar.GetStartOfDayUtc(new DateOnly(2026, 9, 30));

        Assert.Equal(DateTimeOffset.Parse("2026-09-30T03:00:00Z"), start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void GetStartOfDayUtc_SkippedMidnight_ReturnsFirstExistingInstant()
    {
        // Daylight saving time started on 2018-11-04 at 00:00 in
        // America/Sao_Paulo, so local clocks jumped from 23:59:59 to 01:00.
        var date = new DateOnly(2018, 11, 4);
        Assert.True(SaoPaulo.IsInvalidTime(
            date.ToDateTime(TimeOnly.MinValue)));
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.UnixEpoch,
            SaoPaulo);

        DateTimeOffset start = calendar.GetStartOfDayUtc(date);

        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal(date, calendar.GetDate(start));
        Assert.Equal(date.AddDays(-1), calendar.GetDate(start.AddTicks(-1)));
        AssertWithinOneSecondBefore(
            DateTimeOffset.Parse("2018-11-04T03:00:00Z"),
            start);
        Assert.Equal(TimeSpan.FromHours(-2), SaoPaulo.GetUtcOffset(start));
    }

    [Fact]
    public void GetStartOfDayUtc_AfterDaylightSavingEnds_UsesStandardOffset()
    {
        // Daylight saving time ended on 2019-02-17 at 00:00 in
        // America/Sao_Paulo, repeating 23:00-23:59 of 2019-02-16.
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.UnixEpoch,
            SaoPaulo);

        Assert.Equal(
            DateTimeOffset.Parse("2019-02-16T02:00:00Z"),
            calendar.GetStartOfDayUtc(new DateOnly(2019, 2, 16)));
        Assert.Equal(
            DateTimeOffset.Parse("2019-02-17T03:00:00Z"),
            calendar.GetStartOfDayUtc(new DateOnly(2019, 2, 17)));
    }

    [Fact]
    public void GetStartOfDayUtc_AmbiguousMidnight_ReturnsEarliestInstant()
    {
        // America/Havana ended daylight saving time on 2023-11-05 at 01:00,
        // so local 00:00-00:59 happened twice (UTC-4, then UTC-5).
        TimeZoneInfo havana =
            TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        var date = new DateOnly(2023, 11, 5);
        Assert.True(havana.IsAmbiguousTime(
            date.ToDateTime(TimeOnly.MinValue)));
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.UnixEpoch,
            havana);

        DateTimeOffset start = calendar.GetStartOfDayUtc(date);

        Assert.Equal(DateTimeOffset.Parse("2023-11-05T04:00:00Z"), start);
        Assert.Equal(date, calendar.GetDate(start));
        Assert.Equal(date.AddDays(-1), calendar.GetDate(start.AddTicks(-1)));
    }

    [Fact]
    public void GetStartOfDayUtc_SkippedMidnightInConfiguredZone_ReturnsGapEnd()
    {
        // America/Havana started daylight saving time on 2024-03-10 at 00:00.
        TimeZoneInfo havana =
            TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        var date = new DateOnly(2024, 3, 10);
        Assert.True(havana.IsInvalidTime(
            date.ToDateTime(TimeOnly.MinValue)));
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.UnixEpoch,
            havana);

        DateTimeOffset start = calendar.GetStartOfDayUtc(date);

        Assert.Equal(date, calendar.GetDate(start));
        Assert.Equal(date.AddDays(-1), calendar.GetDate(start.AddTicks(-1)));
        AssertWithinOneSecondBefore(
            DateTimeOffset.Parse("2024-03-10T05:00:00Z"),
            start);
    }

    [Fact]
    public void ConfiguredNonDefaultZone_DrivesTodayAndStartOfDay()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        OperationalCalendar calendar = CreateCalendar(
            DateTimeOffset.Parse("2026-09-29T15:00:00Z"),
            tokyo);

        Assert.Same(tokyo, calendar.TimeZone);
        Assert.Equal(new DateOnly(2026, 9, 30), calendar.GetToday());
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-29T15:00:00Z"),
            calendar.GetStartOfDayUtc(new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OperationalCalendar(null!, SaoPaulo));
        Assert.Throws<ArgumentNullException>(
            () => new OperationalCalendar(TimeProvider.System, null!));
    }

    // Windows time zone data encodes some midnight transitions as 23:59:59.999
    // of the previous local day, while IANA tzdata uses exactly 00:00. The
    // first existing instant is therefore the expected gap end, or up to one
    // second earlier depending on the host's time zone database.
    private static void AssertWithinOneSecondBefore(
        DateTimeOffset expected,
        DateTimeOffset actual)
    {
        Assert.InRange(actual, expected.AddSeconds(-1), expected);
    }

    private static OperationalCalendar CreateCalendar(
        DateTimeOffset utcNow,
        TimeZoneInfo timeZone)
    {
        return new OperationalCalendar(
            new RecordingTimeProvider(utcNow),
            timeZone);
    }

    private sealed class RecordingTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            CallCount++;
            return utcNow;
        }
    }
}
