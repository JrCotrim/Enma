using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Enma.Infrastructure.Security;

internal sealed class PwnedPasswordsTelemetryHandler : DelegatingHandler
{
    private static readonly Action<ILogger, int, long, Exception?> LogCompleted =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(1000, "PasswordScreeningDependencyCompleted"),
            "Password screening dependency completed with HTTP status {StatusCode} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, long, Exception?> LogFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(1001, "PasswordScreeningDependencyFailed"),
            "Password screening dependency failed after {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, long, Exception?> LogCanceled =
        LoggerMessage.Define<long>(
            LogLevel.Information,
            new EventId(1002, "PasswordScreeningDependencyCanceled"),
            "Password screening dependency was canceled after {ElapsedMilliseconds} ms.");

    private readonly ILogger<PwnedPasswordsTelemetryHandler> logger;

    public PwnedPasswordsTelemetryHandler(
        ILogger<PwnedPasswordsTelemetryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            HttpResponseMessage response = await base.SendAsync(
                request,
                cancellationToken);
            LogCompleted(
                logger,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                null);

            return response;
        }
        catch (OperationCanceledException)
        {
            LogCanceled(logger, stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (HttpRequestException)
        {
            LogFailed(logger, stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (IOException)
        {
            LogFailed(logger, stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (ObjectDisposedException)
        {
            LogFailed(logger, stopwatch.ElapsedMilliseconds, null);
            throw;
        }
        catch (NotSupportedException)
        {
            LogFailed(logger, stopwatch.ElapsedMilliseconds, null);
            throw;
        }
    }
}
