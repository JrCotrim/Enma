using Enma.Application.Notifications;

namespace Enma.UnitTests.Application.Notifications;

public sealed class GenerateNotificationsUseCaseTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        25,
        2,
        30,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_UsesUtcDateOnlyTodayAndTomorrowInclusiveWindow()
    {
        var persistence = new RecordingPersistence();
        var localOffsetNow = new DateTimeOffset(
            2026,
            8,
            24,
            23,
            30,
            0,
            TimeSpan.FromHours(-3));
        var useCase = new GenerateNotificationsUseCase(
            persistence,
            new FixedTimeProvider(localOffsetNow));

        await useCase.ExecuteAsync();

        DateOnly schedulerDate = new(2026, 8, 25);
        Assert.Equal(schedulerDate, persistence.DeadlineCall?.WindowStart);
        Assert.Equal(schedulerDate.AddDays(1), persistence.DeadlineCall?.WindowEnd);
        Assert.Equal(schedulerDate, persistence.TaskCall?.WindowStart);
        Assert.Equal(schedulerDate.AddDays(1), persistence.TaskCall?.WindowEnd);
    }

    [Fact]
    public async Task ExecuteAsync_UsesOpenClosedSixtyMinuteCalendarWindow()
    {
        var persistence = new RecordingPersistence();
        var useCase = new GenerateNotificationsUseCase(
            persistence,
            new FixedTimeProvider(Now));

        await useCase.ExecuteAsync();

        Assert.Equal(Now, persistence.CalendarCall?.WindowStart);
        Assert.Equal(Now.AddMinutes(60), persistence.CalendarCall?.WindowEnd);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesStableGeneratedAtOnceForEverySource()
    {
        var persistence = new RecordingPersistence();
        var timeProvider = new AdvancingTimeProvider(Now);
        var useCase = new GenerateNotificationsUseCase(persistence, timeProvider);

        NotificationGenerationCycleResult result = await useCase.ExecuteAsync();

        Assert.Equal(1, timeProvider.CallCount);
        Assert.Equal(Now, result.GeneratedAt);
        Assert.Equal(Now, persistence.DeadlineCall?.GeneratedAt);
        Assert.Equal(Now, persistence.TaskCall?.GeneratedAt);
        Assert.Equal(Now, persistence.CalendarCall?.GeneratedAt);
    }

    [Fact]
    public async Task ExecuteAsync_InvokesEachSourceOnceAndReturnsSourceResults()
    {
        var persistence = new RecordingPersistence
        {
            DeadlineResult = new NotificationGenerationSourceResult(3, 1),
            TaskResult = new NotificationGenerationSourceResult(5, 2),
            CalendarResult = new NotificationGenerationSourceResult(7, 3)
        };
        var useCase = new GenerateNotificationsUseCase(
            persistence,
            new FixedTimeProvider(Now));

        NotificationGenerationCycleResult result = await useCase.ExecuteAsync();

        Assert.Equal(1, persistence.DeadlineCallCount);
        Assert.Equal(1, persistence.TaskCallCount);
        Assert.Equal(1, persistence.CalendarCallCount);
        Assert.Equal(persistence.DeadlineResult, result.LegalDeadlines);
        Assert.Equal(persistence.TaskResult, result.LegalTasks);
        Assert.Equal(persistence.CalendarResult, result.CalendarEvents);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationFromPersistencePropagatesAndStopsCycle()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var persistence = new RecordingPersistence
        {
            DeadlineException = new OperationCanceledException(cancellation.Token)
        };
        var useCase = new GenerateNotificationsUseCase(
            persistence,
            new FixedTimeProvider(Now));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => useCase.ExecuteAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, persistence.DeadlineCallCount);
        Assert.Equal(0, persistence.TaskCallCount);
        Assert.Equal(0, persistence.CalendarCallCount);
    }

    private sealed class RecordingPersistence : INotificationGenerationPersistence
    {
        public NotificationGenerationSourceResult DeadlineResult { get; init; } =
            new(0, 1);

        public NotificationGenerationSourceResult TaskResult { get; init; } =
            new(0, 1);

        public NotificationGenerationSourceResult CalendarResult { get; init; } =
            new(0, 1);

        public Exception? DeadlineException { get; init; }

        public int DeadlineCallCount { get; private set; }

        public int TaskCallCount { get; private set; }

        public int CalendarCallCount { get; private set; }

        public DateOnlyCall? DeadlineCall { get; private set; }

        public DateOnlyCall? TaskCall { get; private set; }

        public InstantCall? CalendarCall { get; private set; }

        public Task<NotificationGenerationSourceResult>
            GenerateLegalDeadlineRemindersAsync(
                DateOnly schedulerDate,
                DateOnly reminderWindowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            DeadlineCallCount++;
            DeadlineCall = new DateOnlyCall(
                schedulerDate,
                reminderWindowEnd,
                generatedAt,
                cancellationToken);

            return DeadlineException is null
                ? Task.FromResult(DeadlineResult)
                : Task.FromException<NotificationGenerationSourceResult>(
                    DeadlineException);
        }

        public Task<NotificationGenerationSourceResult>
            GenerateLegalTaskRemindersAsync(
                DateOnly schedulerDate,
                DateOnly reminderWindowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            TaskCallCount++;
            TaskCall = new DateOnlyCall(
                schedulerDate,
                reminderWindowEnd,
                generatedAt,
                cancellationToken);
            return Task.FromResult(TaskResult);
        }

        public Task<NotificationGenerationSourceResult>
            GenerateCalendarEventRemindersAsync(
                DateTimeOffset windowStart,
                DateTimeOffset windowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            CalendarCallCount++;
            CalendarCall = new InstantCall(
                windowStart,
                windowEnd,
                generatedAt,
                cancellationToken);
            return Task.FromResult(CalendarResult);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset firstValue)
        : TimeProvider
    {
        public int CallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset value = firstValue.AddMinutes(CallCount);
            CallCount++;
            return value;
        }
    }

    private sealed record DateOnlyCall(
        DateOnly WindowStart,
        DateOnly WindowEnd,
        DateTimeOffset GeneratedAt,
        CancellationToken CancellationToken);

    private sealed record InstantCall(
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd,
        DateTimeOffset GeneratedAt,
        CancellationToken CancellationToken);
}
