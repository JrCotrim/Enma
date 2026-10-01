namespace Enma.Application.Time;

// Resolves civil dates in the single configured operational time zone.
// "Today" and day boundaries follow that zone instead of UTC.
public sealed class OperationalCalendar
{
    // UTC offsets stay within [-12h, +14h]; the margin keeps the gap search
    // bounds strictly outside every possible local midnight.
    private static readonly TimeSpan GapSearchMargin = TimeSpan.FromHours(15);

    private readonly TimeProvider _timeProvider;

    public OperationalCalendar(TimeProvider timeProvider, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);

        _timeProvider = timeProvider;
        TimeZone = timeZone;
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTimeOffset GetUtcNow()
    {
        return _timeProvider.GetUtcNow().ToUniversalTime();
    }

    public DateOnly GetToday()
    {
        return GetDate(GetUtcNow());
    }

    public DateOnly GetDate(DateTimeOffset instant)
    {
        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);
    }

    // Returns the first existing instant of the operational date. When local
    // midnight is skipped by a daylight saving gap, that is the gap end; when
    // local midnight is ambiguous, it is the earlier of the two instants.
    public DateTimeOffset GetStartOfDayUtc(DateOnly date)
    {
        DateTime localMidnight = date.ToDateTime(
            TimeOnly.MinValue,
            DateTimeKind.Unspecified);

        if (TimeZone.IsAmbiguousTime(localMidnight))
        {
            TimeSpan earliestOffset = TimeZone
                .GetAmbiguousTimeOffsets(localMidnight)
                .Max();

            return new DateTimeOffset(localMidnight, earliestOffset)
                .ToUniversalTime();
        }

        if (!TimeZone.IsInvalidTime(localMidnight))
        {
            return new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localMidnight, TimeZone),
                TimeSpan.Zero);
        }

        return new DateTimeOffset(
            FindFirstUtcInstantAtOrAfter(localMidnight),
            TimeSpan.Zero);
    }

    private DateTime FindFirstUtcInstantAtOrAfter(DateTime localMidnight)
    {
        // Invariant: local(lowerTicks) < localMidnight <= local(upperTicks).
        long lowerTicks = (localMidnight - GapSearchMargin).Ticks;
        long upperTicks = (localMidnight + GapSearchMargin).Ticks;

        while (upperTicks - lowerTicks > 1)
        {
            long middleTicks = lowerTicks + ((upperTicks - lowerTicks) / 2);
            DateTime middleLocal = TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(middleTicks, DateTimeKind.Utc),
                TimeZone);

            if (middleLocal >= localMidnight)
            {
                upperTicks = middleTicks;
            }
            else
            {
                lowerTicks = middleTicks;
            }
        }

        return new DateTime(upperTicks, DateTimeKind.Utc);
    }
}
