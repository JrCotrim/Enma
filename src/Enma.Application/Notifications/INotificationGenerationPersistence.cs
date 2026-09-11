namespace Enma.Application.Notifications;

public interface INotificationGenerationPersistence
{
    Task<NotificationGenerationSourceResult> GenerateLegalDeadlineRemindersAsync(
        DateOnly schedulerDate,
        DateOnly reminderWindowEnd,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    Task<NotificationGenerationSourceResult> GenerateLegalTaskRemindersAsync(
        DateOnly schedulerDate,
        DateOnly reminderWindowEnd,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    Task<NotificationGenerationSourceResult> GenerateCalendarEventRemindersAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    Task<NotificationGenerationSourceResult>
        GeneratePaymentInstallmentDueTodayAsync(
            DateOnly schedulerDate,
            DateTimeOffset generatedAt,
            CancellationToken cancellationToken);
}
