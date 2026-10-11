using System.Data.Common;
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (NotificationGenerationTransientException exception)
            {
                logger.LogWarning(
                    "Notification generation cycle skipped after transient " +
                    "failure {ClassificationCode}",
                    exception.ClassificationCode);
            }
            catch (Exception exception)
            {
                // Only the type and SQLSTATE are logged: messages and provider
                // details can carry row values.
                logger.LogError(
                    "Notification generation cycle failed with {FailureType} " +
                    "and SQLSTATE {SqlState}; the next cycle will retry",
                    exception.GetType().Name,
                    FindSqlState(exception));
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
        LogSourceResult(
            "PaymentInstallment",
            result.PaymentInstallments,
            duration);
        logger.LogInformation(
            "Notification generation cycle completed in {DurationMilliseconds} ms",
            duration.TotalMilliseconds);
    }

    private static string? FindSqlState(Exception exception)
    {
        for (Exception? current = exception;
            current is not null;
            current = current.InnerException)
        {
            if (current is DbException { SqlState: { } sqlState })
            {
                return sqlState;
            }
        }

        return null;
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
