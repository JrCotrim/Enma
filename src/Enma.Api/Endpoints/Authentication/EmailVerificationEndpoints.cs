using System.Diagnostics;
using Enma.Api.Contracts.Authentication;
using Enma.Application.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Enma.Api.Endpoints.Authentication;

public static class EmailVerificationEndpoints
{
    private const string InvalidVerificationCode =
        "email_verification_invalid";

    public static IEndpointRouteBuilder MapEmailVerificationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/auth/email-verification")
            .WithTags("Authentication");

        group.MapPost(
                "/resend",
                async Task<Accepted> (
                    RequestEmailVerificationRequest request,
                    RequestEmailVerificationUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    PreventResponseCaching(httpContext);
                    await useCase.ExecuteAsync(
                        request.Email,
                        cancellationToken);

                    return TypedResults.Accepted((string?)null);
                })
            .WithName("RequestEmailVerification")
            .WithSummary("Requests an email verification message.")
            .Accepts<RequestEmailVerificationRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost(
                "/verify",
                async Task<Results<NoContent, ProblemHttpResult>> (
                    VerifyEmailRequest request,
                    VerifyEmailUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    PreventResponseCaching(httpContext);
                    VerifyEmailResult result = await useCase.ExecuteAsync(
                        request.Token,
                        cancellationToken);

                    if (result == VerifyEmailResult.Succeeded)
                    {
                        return TypedResults.NoContent();
                    }

                    return CreateInvalidVerificationProblem(httpContext);
                })
            .WithName("VerifyEmail")
            .WithSummary("Verifies an email address.")
            .Accepts<VerifyEmailRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static void PreventResponseCaching(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
    }

    private static ProblemHttpResult CreateInvalidVerificationProblem(
        HttpContext httpContext)
    {
        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid email verification",
            Detail = "The email verification request is invalid.",
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["code"] = InvalidVerificationCode;
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return TypedResults.Problem(problemDetails);
    }
}
