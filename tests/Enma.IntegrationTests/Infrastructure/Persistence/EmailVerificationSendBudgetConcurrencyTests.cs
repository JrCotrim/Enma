using System.Data;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailVerificationSendBudgetConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryAcquireAsync_CompetingGlobalAcquisitions_NeverOverAdmits()
    {
        const int contenderCount = 8;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await blockerContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE email_verification_send_budgets IN SHARE MODE",
            timeout.Token);

        Task<bool>[] acquisitionTasks = Enumerable.Range(0, contenderCount)
            .Select(index => AcquireAsync(index, timeout.Token))
            .ToArray();

        try
        {
            await WaitForBlockedAcquisitionsAsync(
                contenderCount,
                timeout.Token);
            await blockerTransaction.CommitAsync(timeout.Token);

            bool[] results = await Task.WhenAll(acquisitionTasks)
                .WaitAsync(timeout.Token);

            Assert.Equal(3, results.Count(admitted => admitted));
            Assert.Equal(contenderCount - 3, results.Count(admitted => !admitted));
        }
        finally
        {
            if (blockerTransaction.GetDbTransaction().Connection is not null)
            {
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            }

            try
            {
                await Task.WhenAll(acquisitionTasks)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (acquisitionTasks.All(task => task.IsCompleted))
            {
            }
        }
    }

    private async Task<bool> AcquireAsync(
        int index,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget =
            EmailVerificationSendBudgetPersistenceTests.CreateBudget(
                dbContext,
                globalLimit: 3,
                destinationLimit: ContenderDestinationLimit);

        return await budget.TryAcquireAsync(
            $"concurrent-{index}@example.test",
            cancellationToken);
    }

    private const int ContenderDestinationLimit = 8;

    private async Task WaitForBlockedAcquisitionsAsync(
        int minimumCount,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE '%INSERT%'
                      AND query ILIKE '%email_verification_send_budgets%'
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount >= minimumCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }
}
