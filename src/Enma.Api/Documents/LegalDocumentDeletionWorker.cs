using Enma.Application.Documents.Delete;

namespace Enma.Api.Documents;

internal sealed class LegalDocumentDeletionWorker(
    IServiceScopeFactory scopeFactory,
    ILegalDocumentDeletionCycleDelay cycleDelay,
    ILogger<LegalDocumentDeletionWorker> logger) : BackgroundService
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
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Legal document deletion cycle deferred after {FailureType}",
                    exception.GetType().Name);
            }
        }
        while (await cycleDelay.WaitForNextCycleAsync(stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ProcessLegalDocumentDeletionsUseCase useCase = scope.ServiceProvider
            .GetRequiredService<ProcessLegalDocumentDeletionsUseCase>();
        ProcessLegalDocumentDeletionsResult result =
            await useCase.ExecuteAsync(cancellationToken);

        logger.LogInformation(
            "Legal document deletion cycle found {FoundCount}, completed " +
            "{CompletedCount}, and deferred {DeferredCount} documents",
            result.FoundCount,
            result.CompletedCount,
            result.DeferredCount);
    }
}
