using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Enma.Application.Security;
using Enma.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class PwnedPasswordsHttpClientLoggingTests
{
    private const string SafeUnavailableMessage =
        "Password compromise screening is temporarily unavailable.";

    [Fact]
    public async Task RegisteredClient_SuccessfulLookup_LogsOnlySanitizedTelemetry()
    {
        string syntheticPassword = CreateSyntheticPassword("SuccessfulLookup");
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        string providerResponseMarker = CreateProviderResponseMarker(hashParts.Suffix);
        string responseBody = string.Join(
            '\n',
            $"{hashParts.Suffix}:0",
            $"{providerResponseMarker}:1");
        using var loggerProvider = new CapturingLoggerProvider();
        using var primaryHandler = new FakePrimaryHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/plain")
            }));
        await using ServiceProvider serviceProvider = CreateServiceProvider(
            loggerProvider,
            primaryHandler);
        ICompromisedPasswordChecker checker = serviceProvider
            .GetRequiredService<ICompromisedPasswordChecker>();
        loggerProvider.Clear();

        bool isCompromised = await checker.IsCompromisedAsync(syntheticPassword);

        IReadOnlyList<LogEntry> entries = loggerProvider.Entries;
        Assert.False(isCompromised);
        Assert.Equal(1, primaryHandler.RequestCount);
        Assert.True(
            string.Equals(
                primaryHandler.AbsolutePath,
                $"/range/{hashParts.Prefix}",
                StringComparison.Ordinal),
            "The request path did not use the expected k-anonymity lookup shape.");
        Assert.True(
            Regex.IsMatch(
                primaryHandler.AbsolutePath,
                "^/range/[0-9A-F]{5}$",
                RegexOptions.CultureInvariant),
            "The request path did not contain exactly five hexadecimal characters.");
        Assert.Equal(string.Empty, primaryHandler.Query);
        Assert.False(primaryHandler.HasContent);
        Assert.True(
            primaryHandler.AddPaddingValues.SequenceEqual(
                ["true"],
                StringComparer.Ordinal),
            "The production Add-Padding header was not preserved.");
        Assert.Equal("ENMA/1.0", primaryHandler.UserAgent);
        Assert.Contains("text/plain", primaryHandler.AcceptMediaTypes);
        AssertNoDefaultHttpClientLogs(entries);
        AssertNoSensitiveLogContent(
            entries,
            syntheticPassword,
            hashParts.CompleteHash,
            hashParts.Prefix,
            hashParts.Suffix,
            primaryHandler.RequestUriText,
            "/range/",
            providerResponseMarker,
            responseBody);
        Assert.False(
            entries.Any(entry => entry.FormattedException is not null),
            "A logger entry unexpectedly included an exception.");
        Assert.True(
            entries.Count == 1,
            "The successful request did not emit exactly one sanitized log entry.");
        Assert.Equal(
            1,
            entries.Count(entry =>
                entry.Level == LogLevel.Information &&
                entry.EventId.Id == 1000 &&
                entry.EventId.Name == "PasswordScreeningDependencyCompleted"));
        LogEntry telemetryEntry = entries.First(entry => entry.EventId.Id == 1000);
        Assert.True(
            Regex.IsMatch(
                telemetryEntry.Message,
                "^Password screening dependency completed with HTTP status 200 in [0-9]+ ms\\.$",
                RegexOptions.CultureInvariant),
            "The successful dependency telemetry did not use the fixed safe template.");
    }

    [Fact]
    public async Task RegisteredClient_TransportFailure_DoesNotLogRequestOrExceptionDiagnostics()
    {
        string syntheticPassword = CreateSyntheticPassword("TransportFailure");
        HashParts hashParts = CalculateHashParts(syntheticPassword);
        const string transportDiagnosticMarker =
            "SyntheticTransportDiagnosticMarker";
        string transportExceptionMessage = string.Empty;
        using var loggerProvider = new CapturingLoggerProvider();
        using var primaryHandler = new FakePrimaryHandler(
            (request, _) =>
            {
                string requestUri = request.RequestUri?.ToString() ?? string.Empty;
                transportExceptionMessage = string.Join(
                    '|',
                    requestUri,
                    hashParts.Prefix,
                    hashParts.Suffix,
                    hashParts.CompleteHash,
                    transportDiagnosticMarker);

                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException(transportExceptionMessage));
            });
        await using ServiceProvider serviceProvider = CreateServiceProvider(
            loggerProvider,
            primaryHandler);
        ICompromisedPasswordChecker checker = serviceProvider
            .GetRequiredService<ICompromisedPasswordChecker>();
        loggerProvider.Clear();
        CompromisedPasswordCheckUnavailableException? unavailableException = null;
        bool unexpectedException = false;

        try
        {
            await checker.IsCompromisedAsync(syntheticPassword);
        }
        catch (CompromisedPasswordCheckUnavailableException exception)
        {
            unavailableException = exception;
        }
        catch
        {
            unexpectedException = true;
        }

        IReadOnlyList<LogEntry> entries = loggerProvider.Entries;
        Assert.False(
            unexpectedException,
            "The registered adapter propagated an unexpected exception type.");
        Assert.NotNull(unavailableException);
        Assert.Equal(SafeUnavailableMessage, unavailableException.Message);
        Assert.Null(unavailableException.InnerException);
        Assert.Equal(1, primaryHandler.RequestCount);
        Assert.False(
            string.IsNullOrEmpty(transportExceptionMessage),
            "The deterministic transport failure was not exercised.");
        AssertNoDefaultHttpClientLogs(entries);
        AssertNoSensitiveLogContent(
            entries,
            syntheticPassword,
            primaryHandler.RequestUriText,
            "/range/",
            hashParts.Prefix,
            hashParts.Suffix,
            hashParts.CompleteHash,
            transportDiagnosticMarker,
            transportExceptionMessage,
            "HttpRequestException");
        Assert.False(
            entries.Any(entry => entry.FormattedException is not null),
            "A logger entry unexpectedly included transport exception diagnostics.");
        Assert.True(
            entries.Count == 1,
            "The failed request did not emit exactly one sanitized log entry.");
        Assert.Equal(
            1,
            entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.EventId.Id == 1001 &&
                entry.EventId.Name == "PasswordScreeningDependencyFailed"));
        LogEntry telemetryEntry = entries.First(entry => entry.EventId.Id == 1001);
        Assert.True(
            Regex.IsMatch(
                telemetryEntry.Message,
                "^Password screening dependency failed after [0-9]+ ms\\.$",
                RegexOptions.CultureInvariant),
            "The failed dependency telemetry did not use the fixed safe template.");
    }

    private static ServiceProvider CreateServiceProvider(
        CapturingLoggerProvider loggerProvider,
        HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(loggerProvider);
        });
        services.AddInfrastructure(nameof(PwnedPasswordsHttpClientLoggingTests));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
            new PrimaryHandlerReplacementFilter(primaryHandler));

        return services.BuildServiceProvider();
    }

    private static string CreateSyntheticPassword(string scenario)
    {
        return string.Concat("Synthetic-", scenario, "-Test-Only!");
    }

    private static HashParts CalculateHashParts(string password)
    {
        byte[]? passwordBytes = null;
        byte[]? hashBytes = null;

        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(password);
            hashBytes = SHA1.HashData(passwordBytes);
            string completeHash = Convert.ToHexString(hashBytes);

            return new HashParts(
                completeHash[..5],
                completeHash[5..],
                completeHash);
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            if (hashBytes is not null)
            {
                CryptographicOperations.ZeroMemory(hashBytes);
            }
        }
    }

    private static string CreateProviderResponseMarker(string suffix)
    {
        string marker = new('A', 35);

        return string.Equals(marker, suffix, StringComparison.OrdinalIgnoreCase)
            ? new string('B', 35)
            : marker;
    }

    private static void AssertNoDefaultHttpClientLogs(
        IReadOnlyList<LogEntry> entries)
    {
        Assert.False(
            entries.Any(entry => entry.Category.StartsWith(
                "System.Net.Http.HttpClient.",
                StringComparison.Ordinal)),
            "Default HttpClientFactory logging was emitted.");
    }

    private static void AssertNoSensitiveLogContent(
        IReadOnlyList<LogEntry> entries,
        params string[] sensitiveValues)
    {
        foreach (string sensitiveValue in sensitiveValues)
        {
            Assert.False(
                entries.Any(entry =>
                    entry.Category.Contains(
                        sensitiveValue,
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.Message.Contains(
                        sensitiveValue,
                        StringComparison.OrdinalIgnoreCase) ||
                    (entry.FormattedException?.Contains(
                        sensitiveValue,
                        StringComparison.OrdinalIgnoreCase) ?? false)),
                "A logger entry contained prohibited request or provider material.");
        }
    }

    private sealed record HashParts(
        string Prefix,
        string Suffix,
        string CompleteHash);

    private sealed class PrimaryHandlerReplacementFilter(
        HttpMessageHandler primaryHandler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(
            Action<HttpMessageHandlerBuilder> next)
        {
            return builder =>
            {
                next(builder);
                builder.PrimaryHandler = primaryHandler;
            };
        }
    }

    private sealed class FakePrimaryHandler(
        Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string AbsolutePath { get; private set; } = string.Empty;

        public string Query { get; private set; } = string.Empty;

        public string RequestUriText { get; private set; } = string.Empty;

        public bool HasContent { get; private set; }

        public string[] AddPaddingValues { get; private set; } = [];

        public string UserAgent { get; private set; } = string.Empty;

        public string[] AcceptMediaTypes { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            AbsolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
            Query = request.RequestUri?.Query ?? string.Empty;
            RequestUriText = request.RequestUri?.ToString() ?? string.Empty;
            HasContent = request.Content is not null;
            AddPaddingValues = request.Headers.TryGetValues(
                "Add-Padding",
                out IEnumerable<string>? values)
                ? values.ToArray()
                : [];
            UserAgent = request.Headers.UserAgent.ToString();
            AcceptMediaTypes = request.Headers.Accept
                .Select(value => value.MediaType)
                .OfType<string>()
                .ToArray();

            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, entries);
        }

        public void Clear()
        {
            while (entries.TryDequeue(out _))
            {
            }
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullScope.Instance;
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
                entries.Enqueue(new LogEntry(
                    category,
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception?.ToString()));
            }
        }
    }

    private sealed class LogEntry(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        string? formattedException)
    {
        public string Category { get; } = category;

        public LogLevel Level { get; } = level;

        public EventId EventId { get; } = eventId;

        public string Message { get; } = message;

        public string? FormattedException { get; } = formattedException;
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
