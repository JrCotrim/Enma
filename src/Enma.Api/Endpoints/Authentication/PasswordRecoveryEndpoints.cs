using System.Diagnostics;
using Enma.Api.Contracts.Authentication;
using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Application.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Enma.Api.Endpoints.Authentication;

public static class PasswordRecoveryEndpoints
{
    internal const string RequestRateLimitPolicy = "PasswordRecoveryRequest";
    internal const string ResetRateLimitPolicy = "PasswordRecoveryReset";

    public static IEndpointRouteBuilder MapPasswordRecoveryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints
            .MapGroup("/api/auth/password-recovery")
            .WithTags("Authentication");

        group.MapPost(
                "/request",
                async Task<Accepted> (
                    RequestPasswordRecoveryRequest request,
                    RequestPasswordRecoveryUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    PreventResponseCaching(httpContext);
                    await useCase.ExecuteAsync(request.Email, cancellationToken);
                    return TypedResults.Accepted((string?)null);
                })
            .WithName("RequestPasswordRecovery")
            .WithSummary("Requests password recovery instructions.")
            .Accepts<RequestPasswordRecoveryRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(RequestRateLimitPolicy);

        group.MapPost(
                "/reset",
                async Task<Results<NoContent, ProblemHttpResult>> (
                    ResetPasswordRequest request,
                    ResetPasswordUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    PreventResponseCaching(httpContext);

                    try
                    {
                        ResetPasswordResult result = await useCase.ExecuteAsync(
                            request.Token,
                            request.NewPassword,
                            cancellationToken);

                        return result switch
                        {
                            ResetPasswordResult.Succeeded =>
                                TypedResults.NoContent(),
                            ResetPasswordResult.CurrentPasswordReuse =>
                                CreateProblem(
                                    httpContext,
                                    StatusCodes.Status400BadRequest,
                                    "Invalid password",
                                    "A nova senha deve ser diferente da senha atual.",
                                    "password_current_reuse"),
                            _ => CreateProblem(
                                httpContext,
                                StatusCodes.Status400BadRequest,
                                "Invalid password recovery",
                                "The password recovery request is invalid or expired.",
                                "password_recovery_invalid")
                        };
                    }
                    catch (CompromisedPasswordException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status400BadRequest,
                            "Invalid password",
                            exception.Message,
                            "password_compromised");
                    }
                    catch (CompromisedPasswordCheckUnavailableException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status503ServiceUnavailable,
                            "Password screening unavailable",
                            exception.Message,
                            "password_screening_unavailable");
                    }
                    catch (RequestValidationException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status400BadRequest,
                            "Invalid password",
                            exception.Message,
                            "password_invalid");
                    }
                })
            .WithName("ResetPassword")
            .WithSummary("Resets a password using a single-use recovery token.")
            .Accepts<ResetPasswordRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(ResetRateLimitPolicy);

        return endpoints;
    }

    private static void PreventResponseCaching(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = "no-store";

    private static ProblemHttpResult CreateProblem(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail,
        string code)
    {
        ProblemDetails problemDetails = new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["code"] = code;
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;
        return TypedResults.Problem(problemDetails);
    }
}
