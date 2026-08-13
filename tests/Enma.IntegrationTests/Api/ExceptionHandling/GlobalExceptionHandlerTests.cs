using System.Text.Json;
using Enma.Api.ExceptionHandling;
using Enma.Application.Organizations.Create;
using Enma.Application.Organizations.GetById;
using Enma.Application.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Enma.IntegrationTests.Api.ExceptionHandling;

public sealed class GlobalExceptionHandlerTests
{
    private const string InternalMessage = "Synthetic internal invariant detail.";
    private const string GenericDetail =
        "An unexpected error occurred while processing the request.";

    [Fact]
    public async Task TryHandleAsync_WithArgumentException_ReturnsGeneric500AndLogsError()
    {
        var exception = new ArgumentException(InternalMessage, "internalState");

        HandlerResult result = await HandleAsync(exception);

        AssertUnexpectedFailure(result, exception);
    }

    [Fact]
    public async Task TryHandleAsync_WithArgumentNullException_ReturnsGeneric500AndLogsError()
    {
        var exception = new ArgumentNullException("internalState", InternalMessage);

        HandlerResult result = await HandleAsync(exception);

        AssertUnexpectedFailure(result, exception);
    }

    [Fact]
    public async Task TryHandleAsync_WithArgumentOutOfRangeException_ReturnsGeneric500AndLogsError()
    {
        var exception = new ArgumentOutOfRangeException(
            "internalState",
            InternalMessage);

        HandlerResult result = await HandleAsync(exception);

        AssertUnexpectedFailure(result, exception);
    }

    [Fact]
    public async Task TryHandleAsync_WithRequestValidationException_ReturnsControlled400()
    {
        const string PublicMessage = "The request value is invalid.";
        var exception = new RequestValidationException(PublicMessage);

        HandlerResult result = await HandleAsync(exception);

        Assert.True(result.Handled);
        Assert.Equal(StatusCodes.Status400BadRequest, result.ResponseStatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, result.ProblemDetails.Status);
        Assert.Equal("Invalid request data", result.ProblemDetails.Title);
        Assert.Equal(PublicMessage, result.ProblemDetails.Detail);
        Assert.Empty(result.Logger.Entries);
    }

    [Theory]
    [MemberData(nameof(GetExpectedBusinessExceptions))]
    public async Task TryHandleAsync_WithExpectedBusinessException_ReturnsMappedStatusCode(
        Exception exception,
        int expectedStatusCode,
        string expectedTitle)
    {
        HandlerResult result = await HandleAsync(exception);

        Assert.True(result.Handled);
        Assert.Equal(expectedStatusCode, result.ResponseStatusCode);
        Assert.Equal(expectedStatusCode, result.ProblemDetails.Status);
        Assert.Equal(expectedTitle, result.ProblemDetails.Title);
        Assert.Equal(exception.Message, result.ProblemDetails.Detail);
        Assert.Empty(result.Logger.Entries);
    }

    public static TheoryData<Exception, int, string> GetExpectedBusinessExceptions()
    {
        return new TheoryData<Exception, int, string>
        {
            {
                new OrganizationNotFoundException(Guid.NewGuid()),
                StatusCodes.Status404NotFound,
                "Organization not found"
            },
            {
                new OrganizationSlugAlreadyExistsException("enma-legal"),
                StatusCodes.Status409Conflict,
                "Organization slug conflict"
            }
        };
    }

    private static async Task<HandlerResult> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddProblemDetails();
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var logger = new CapturingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(
            serviceProvider.GetRequiredService<IProblemDetailsService>(),
            logger);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            TraceIdentifier = "test-trace-id"
        };
        httpContext.Request.Path = "/test/error";
        httpContext.Response.Body = new MemoryStream();

        bool handled = await handler.TryHandleAsync(
            httpContext,
            exception,
            CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        ProblemDetails? problemDetails = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return new HandlerResult(
            handled,
            httpContext.Response.StatusCode,
            Assert.IsType<ProblemDetails>(problemDetails),
            logger);
    }

    private static void AssertUnexpectedFailure(
        HandlerResult result,
        Exception expectedException)
    {
        Assert.True(result.Handled);
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            result.ResponseStatusCode);
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            result.ProblemDetails.Status);
        Assert.Equal("Unexpected server error", result.ProblemDetails.Title);
        Assert.Equal(GenericDetail, result.ProblemDetails.Detail);
        Assert.DoesNotContain(
            InternalMessage,
            JsonSerializer.Serialize(result.ProblemDetails),
            StringComparison.Ordinal);

        LogEntry entry = Assert.Single(result.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(expectedException, entry.Exception);
    }

    private sealed record HandlerResult(
        bool Handled,
        int ResponseStatusCode,
        ProblemDetails ProblemDetails,
        CapturingLogger<GlobalExceptionHandler> Logger);

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

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
            Entries.Add(new LogEntry(logLevel, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
