using System.Collections.Concurrent;
using System.Threading.Channels;
using Enma.Api.Notifications;
using Enma.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enma.IntegrationTests.Api.Notifications;

public sealed class NotificationGenerationWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartAsync_RunsOneCycleImmediately()
    {
        var control = new GenerationControl();
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, control.CycleCount);
    }

    [Fact]
    public async Task ReleasedTick_RunsSubsequentScheduledCycle()
    {
        var control = new GenerationControl();
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        delay.ReleaseTick();
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, control.CycleCount);
    }

    [Fact]
    public async Task SequentialTicks_DoNotOverlapAndCreateOneScopePerCycle()
    {
        var control = new GenerationControl();
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        delay.ReleaseTick();
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, control.MaximumConcurrentCycles);
        Assert.Equal(2, control.PersistenceInstanceIds.Count);
    }

    [Fact]
    public async Task StopAsync_CancelsScheduledWaitAndCompletesNormally()
    {
        var control = new GenerationControl();
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();

        Exception? exception = await Record.ExceptionAsync(
            () => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.True(worker.ExecuteTask?.IsCompleted);
    }

    [Fact]
    public async Task TransientCycleFailure_IsSkippedAndNextTickStillRuns()
    {
        var control = new GenerationControl();
        control.EnqueueFailure(
            new NotificationGenerationTransientException(
                "database-timeout",
                new TimeoutException("synthetic transient timeout")));
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        delay.ReleaseTick();
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, control.CycleCount);
        Assert.False(worker.ExecuteTask?.IsFaulted);
    }

    [Fact]
    public async Task UnexpectedCycleFailure_IsNotSwallowed()
    {
        var control = new GenerationControl();
        control.EnqueueFailure(new InvalidOperationException("synthetic failure"));
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        NotificationGenerationWorker worker = CreateWorker(services, delay);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await worker.StartAsync(CancellationToken.None);
                Task executeTask = worker.ExecuteTask
                    ?? throw new InvalidOperationException(
                        "The worker did not expose its execution task.");
                await executeTask;
            });

        Assert.Equal("synthetic failure", exception.Message);
        Assert.Equal(1, control.CycleCount);
    }

    [Fact]
    public void ProductionCadence_IsFiveMinutes()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            PeriodicNotificationGenerationCycleDelay.Cadence);
    }

    private static ServiceProvider CreateServices(GenerationControl control)
    {
        var services = new ServiceCollection();
        services.AddSingleton(control);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<
            INotificationGenerationPersistence,
            ControlledGenerationPersistence>();
        services.AddScoped<GenerateNotificationsUseCase>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static NotificationGenerationWorker CreateWorker(
        ServiceProvider services,
        INotificationGenerationCycleDelay delay)
    {
        return new NotificationGenerationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            delay,
            services.GetRequiredService<TimeProvider>(),
            NullLogger<NotificationGenerationWorker>.Instance);
    }

    private sealed class ManualCycleDelay : INotificationGenerationCycleDelay
    {
        private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();
        private readonly SemaphoreSlim waitStarted = new(0);

        public async ValueTask<bool> WaitForNextCycleAsync(
            CancellationToken cancellationToken)
        {
            waitStarted.Release();
            return await ticks.Reader.ReadAsync(cancellationToken);
        }

        public void ReleaseTick()
        {
            Assert.True(ticks.Writer.TryWrite(true));
        }

        public async Task WaitUntilWaitingAsync()
        {
            Assert.True(await waitStarted.WaitAsync(TestTimeout));
        }
    }

    private sealed class GenerationControl
    {
        private readonly ConcurrentQueue<Exception> failures = new();
        private readonly SemaphoreSlim cycleStarted = new(0);
        private readonly object sync = new();
        private int activeCycles;

        public int CycleCount { get; private set; }

        public int MaximumConcurrentCycles { get; private set; }

        public HashSet<Guid> PersistenceInstanceIds { get; } = [];

        public void EnqueueFailure(Exception exception)
        {
            failures.Enqueue(exception);
        }

        public Exception? StartCycle(Guid persistenceInstanceId)
        {
            lock (sync)
            {
                CycleCount++;
                activeCycles++;
                MaximumConcurrentCycles = Math.Max(
                    MaximumConcurrentCycles,
                    activeCycles);
                PersistenceInstanceIds.Add(persistenceInstanceId);
            }

            cycleStarted.Release();
            return failures.TryDequeue(out Exception? exception)
                ? exception
                : null;
        }

        public void CompleteCycle()
        {
            lock (sync)
            {
                activeCycles--;
            }
        }

        public async Task WaitForCycleAsync()
        {
            Assert.True(await cycleStarted.WaitAsync(TestTimeout));
        }
    }

    private sealed class ControlledGenerationPersistence(GenerationControl control)
        : INotificationGenerationPersistence
    {
        private readonly Guid instanceId = Guid.NewGuid();

        public Task<NotificationGenerationSourceResult>
            GenerateLegalDeadlineRemindersAsync(
                DateOnly schedulerDate,
                DateOnly reminderWindowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            Exception? exception = control.StartCycle(instanceId);

            if (exception is not null)
            {
                control.CompleteCycle();
                return Task.FromException<NotificationGenerationSourceResult>(
                    exception);
            }

            return Task.FromResult(new NotificationGenerationSourceResult(0, 1));
        }

        public Task<NotificationGenerationSourceResult>
            GenerateLegalTaskRemindersAsync(
                DateOnly schedulerDate,
                DateOnly reminderWindowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            return Task.FromResult(new NotificationGenerationSourceResult(0, 1));
        }

        public Task<NotificationGenerationSourceResult>
            GenerateCalendarEventRemindersAsync(
                DateTimeOffset windowStart,
                DateTimeOffset windowEnd,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            control.CompleteCycle();
            return Task.FromResult(new NotificationGenerationSourceResult(0, 1));
        }
    }
}
