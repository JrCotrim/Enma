using Enma.Api.Contracts.Authentication;
using Enma.Application.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace Enma.Api.Endpoints.Authentication;

public static class LoginEndpoints
{
    internal const string RateLimitPolicy = "AuthenticationLogin";

    public static IEndpointRouteBuilder MapLoginEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost(
                "/api/auth/login",
                async Task<Results<NoContent, UnauthorizedHttpResult>> (
                    LoginRequest request,
                    LoginUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                    LoginResult result = await useCase.ExecuteAsync(
                        request.Email,
                        request.Password,
                        cancellationToken);

                    if (result.Status == LoginResultStatus.InvalidCredentials)
                    {
                        return TypedResults.Unauthorized();
                    }

                    string sessionHandle = result.SessionHandle
                        ?? throw new InvalidOperationException(
                            "A successful login did not provide a session handle.");
                    httpContext.Response.Cookies.Append(
                        AuthenticationCookies.SessionName,
                        sessionHandle,
                        AuthenticationCookies.CreateSessionOptions());

                    return TypedResults.NoContent();
                })
            .WithName("Login")
            .WithSummary("Creates a server-managed authentication session.")
            .WithTags("Authentication")
            .Accepts<LoginRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(RateLimitPolicy);

        return endpoints;
    }
}
