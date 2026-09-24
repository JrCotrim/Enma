namespace Enma.Api.Documents;

internal interface ILegalDocumentDeletionCycleDelay
{
    ValueTask<bool> WaitForNextCycleAsync(CancellationToken cancellationToken);
}

internal sealed class PeriodicLegalDocumentDeletionCycleDelay(
    TimeProvider timeProvider) : ILegalDocumentDeletionCycleDelay, IDisposable
{
    internal static readonly TimeSpan Cadence = TimeSpan.FromMinutes(1);

    private readonly PeriodicTimer timer = new(Cadence, timeProvider);

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
