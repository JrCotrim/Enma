using Enma.Application.Notifications;

namespace Enma.Api.Notifications;

internal sealed class NotificationGenerationWorker(
    IServiceScopeFactory scopeFactory,
    INotificationGenerationCycleDelay cycleDelay,
    TimeProvider timeProvider,
    ILogger<NotificationGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (NotificationGenerationTransientException exception)
            {
                logger.LogWarning(
                    "Notification generation cycle skipped after transient " +
                    "failure {ClassificationCode}",
                    exception.ClassificationCode);
            }
        }
        while (await cycleDelay.WaitForNextCycleAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        long startedAt = timeProvider.GetTimestamp();
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        GenerateNotificationsUseCase useCase = scope.ServiceProvider
            .GetRequiredService<GenerateNotificationsUseCase>();

        NotificationGenerationCycleResult result =
            await useCase.ExecuteAsync(cancellationToken);
        TimeSpan duration = timeProvider.GetElapsedTime(startedAt);

        LogSourceResult("LegalDeadline", result.LegalDeadlines, duration);
        LogSourceResult("LegalTask", result.LegalTasks, duration);
        LogSourceResult("CalendarEvent", result.CalendarEvents, duration);
        logger.LogInformation(
            "Notification generation cycle completed in {DurationMilliseconds} ms",
            duration.TotalMilliseconds);
    }

    private void LogSourceResult(
        string source,
        NotificationGenerationSourceResult result,
        TimeSpan duration)
    {
        logger.LogInformation(
            "Notification generation source {Source} inserted {InsertedCount} " +
            "rows in {BatchCount} batches during a {DurationMilliseconds} ms cycle",
            source,
            result.InsertedCount,
            result.BatchCount,
            duration.TotalMilliseconds);
    }
}
