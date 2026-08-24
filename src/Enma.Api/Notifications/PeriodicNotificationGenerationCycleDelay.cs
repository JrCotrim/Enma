namespace Enma.Api.Notifications;

internal sealed class PeriodicNotificationGenerationCycleDelay :
    INotificationGenerationCycleDelay,
    IDisposable
{
    internal static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    private readonly PeriodicTimer timer;

    public PeriodicNotificationGenerationCycleDelay(TimeProvider timeProvider)
    {
        timer = new PeriodicTimer(Cadence, timeProvider);
    }

    public ValueTask<bool> WaitForNextCycleAsync(
        CancellationToken cancellationToken)
    {
        return timer.WaitForNextTickAsync(cancellationToken);
    }

    public void Dispose()
    {
        timer.Dispose();
    }
}
