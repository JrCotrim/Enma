namespace Enma.Application.Notifications;

public sealed class GenerateNotificationsUseCase(
    INotificationGenerationPersistence persistence,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan CalendarEventReminderWindow =
        TimeSpan.FromMinutes(60);

    public async Task<NotificationGenerationCycleResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        DateOnly schedulerDate = DateOnly.FromDateTime(now.UtcDateTime);
        DateOnly reminderWindowEnd = schedulerDate.AddDays(1);

        NotificationGenerationSourceResult legalDeadlines =
            await persistence.GenerateLegalDeadlineRemindersAsync(
                schedulerDate,
                reminderWindowEnd,
                now,
                cancellationToken);
        NotificationGenerationSourceResult legalTasks =
            await persistence.GenerateLegalTaskRemindersAsync(
                schedulerDate,
                reminderWindowEnd,
                now,
                cancellationToken);
        NotificationGenerationSourceResult calendarEvents =
            await persistence.GenerateCalendarEventRemindersAsync(
                now,
                now.Add(CalendarEventReminderWindow),
                now,
                cancellationToken);

        return new NotificationGenerationCycleResult(
            now,
            legalDeadlines,
            legalTasks,
            calendarEvents);
    }
}
