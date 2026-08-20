using System.Diagnostics;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Application.Organizations.Create;
using Enma.Application.Organizations.GetById;
using Enma.Application.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Enma.Api.ExceptionHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsService);
        ArgumentNullException.ThrowIfNull(logger);

        this.problemDetailsService = problemDetailsService;
        this.logger = logger;
    }

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException &&
            httpContext.RequestAborted.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        (int statusCode, string title, string detail) = exception switch
        {
            LegalDocumentUploadOutcomeUnknownException => (
                StatusCodes.Status500InternalServerError,
                "Document upload outcome unknown",
                "The upload may have succeeded, but its outcome could not be confirmed. Do not retry automatically; check the document list before taking further action."),
            LegalDocumentContentStagingUnavailableException or
            LegalDocumentContentInspectionUnavailableException or
            LegalDocumentStorageException or
            LegalDocumentUploadCompensationUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Document upload unavailable",
                "The document upload service is temporarily unavailable."),
            OrganizationNotFoundException => (
                StatusCodes.Status404NotFound,
                "Organization not found",
                exception.Message),
            OrganizationSlugAlreadyExistsException => (
                StatusCodes.Status409Conflict,
                "Organization slug conflict",
                exception.Message),
            RequestValidationException => (
                StatusCodes.Status400BadRequest,
                "Invalid request data",
                exception.Message),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                "An unexpected error occurred while processing the request.")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "An unexpected exception occurred while processing the request.");
        }

        httpContext.Response.StatusCode = statusCode;

        ProblemDetails problemDetails = new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }
}
