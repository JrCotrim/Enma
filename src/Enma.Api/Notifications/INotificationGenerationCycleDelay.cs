namespace Enma.Api.Notifications;

internal interface INotificationGenerationCycleDelay
{
    ValueTask<bool> WaitForNextCycleAsync(CancellationToken cancellationToken);
}
