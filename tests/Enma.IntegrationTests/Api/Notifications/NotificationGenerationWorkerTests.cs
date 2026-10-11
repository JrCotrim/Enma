using System.Collections.Concurrent;
using System.Threading.Channels;
using Enma.Api.Notifications;
using Enma.Application.Notifications;
using Enma.Application.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

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
        Assert.Equal(1, control.PaymentInstallmentCallCount);
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
    public async Task UnexpectedCycleFailure_IsLoggedAndNextTickStillRuns()
    {
        var control = new GenerationControl();
        control.EnqueueFailure(new InvalidOperationException("synthetic failure"));
        await using ServiceProvider services = CreateServices(control);
        var delay = new ManualCycleDelay();
        var logs = new CapturingLoggerProvider();
        NotificationGenerationWorker worker = CreateWorker(services, delay, logs);

        await worker.StartAsync(CancellationToken.None);
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        delay.ReleaseTick();
        await control.WaitForCycleAsync();
        await delay.WaitUntilWaitingAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, control.CycleCount);
        Assert.False(worker.ExecuteTask?.IsFaulted);
        CapturedLogEntry failure = Assert.Single(
            logs.Entries,
            entry => entry.Level == LogLevel.Error);
        Assert.Contains(
            nameof(InvalidOperationException),
            failure.Message,
            StringComparison.Ordinal);
        Assert.Null(failure.Exception);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Contains("synthetic failure"));
    }

    [Fact]
    public async Task NonTransientPostgresFailure_DoesNotStopHostAndNextCycleRuns()
    {
        const string syntheticRelation = "synthetic_sensitive_relation";
        const string syntheticRowValue = "sensitive-row@example.test";
        var control = new GenerationControl();
        control.EnqueueFailure(new PostgresException(
            messageText: $"relation {syntheticRelation} does not exist",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UndefinedTable,
            detail: $"Key (email)=({syntheticRowValue}) is referenced."));
        var delay = new ManualCycleDelay();
        var logs = new CapturingLoggerProvider();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost);
        AddGenerationServices(builder.Services, control);
        builder.Services.AddSingleton<INotificationGenerationCycleDelay>(delay);
        builder.Services.AddHostedService<NotificationGenerationWorker>();
        using IHost host = builder.Build();
        IHostApplicationLifetime lifetime = host.Services
            .GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();
        try
        {
            await control.WaitForCycleAsync();
            await delay.WaitUntilWaitingAsync();
            delay.ReleaseTick();
            await control.WaitForCycleAsync();
            await delay.WaitUntilWaitingAsync();

            Assert.Equal(2, control.CycleCount);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await host.StopAsync();
        }

        CapturedLogEntry failure = Assert.Single(
            logs.Entries,
            entry => entry.Category == typeof(NotificationGenerationWorker).FullName &&
                entry.Level == LogLevel.Error);
        Assert.Contains(nameof(PostgresException), failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            PostgresErrorCodes.UndefinedTable,
            failure.Message,
            StringComparison.Ordinal);
        Assert.Null(failure.Exception);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Contains(syntheticRelation) ||
                entry.Contains(syntheticRowValue));
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
        AddGenerationServices(services, control);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static void AddGenerationServices(
        IServiceCollection services,
        GenerationControl control)
    {
        services.AddSingleton(control);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(serviceProvider => new OperationalCalendar(
            serviceProvider.GetRequiredService<TimeProvider>(),
            TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")));
        services.AddScoped<
            INotificationGenerationPersistence,
            ControlledGenerationPersistence>();
        services.AddScoped<GenerateNotificationsUseCase>();
    }

    private static NotificationGenerationWorker CreateWorker(
        ServiceProvider services,
        INotificationGenerationCycleDelay delay,
        ILoggerFactory? loggerFactory = null)
    {
        return new NotificationGenerationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            delay,
            services.GetRequiredService<TimeProvider>(),
            loggerFactory is null
                ? NullLogger<NotificationGenerationWorker>.Instance
                : new Logger<NotificationGenerationWorker>(loggerFactory));
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

        public int PaymentInstallmentCallCount { get; private set; }

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

        public void CompleteFinanceSource()
        {
            lock (sync)
            {
                PaymentInstallmentCallCount++;
            }

            CompleteCycle();
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
            return Task.FromResult(new NotificationGenerationSourceResult(0, 1));
        }

        public Task<NotificationGenerationSourceResult>
            GeneratePaymentInstallmentDueTodayAsync(
                DateOnly schedulerDate,
                DateTimeOffset generatedAt,
                CancellationToken cancellationToken)
        {
            control.CompleteFinanceSource();
            return Task.FromResult(new NotificationGenerationSourceResult(0, 1));
        }
    }
}
