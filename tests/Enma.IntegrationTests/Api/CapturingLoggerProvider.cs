using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Enma.IntegrationTests.Api;

internal sealed class CapturingLoggerProvider : ILoggerProvider, ILoggerFactory
{
    private readonly ConcurrentQueue<CapturedLogEntry> entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, entries);
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new CapturedLogEntry(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception));
        }
    }
}

internal sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception)
{
    public bool Contains(string value)
    {
        return Message.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            (Exception?.ToString().Contains(
                value,
                StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
