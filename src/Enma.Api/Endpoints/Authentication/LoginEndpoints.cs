using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Contracts.Authentication;
using Enma.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
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
                async Task<IResult> (
                    LoginRequest request,
                    LoginUseCase useCase,
                    ExternalAuthenticationUseCase externalUseCase,
                    RevokeSessionUseCase revokeSessionUseCase,
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

                    if (request.CompleteGoogleLink)
                    {
                        AuthenticateResult external =
                            await httpContext.AuthenticateAsync(
                                ExternalAuthenticationDefaults.CookieScheme);
                        string? subject = external.Principal?.FindFirstValue(
                            ExternalAuthenticationDefaults.GoogleSubjectClaim);
                        string? email = external.Principal?.FindFirstValue(
                            ExternalAuthenticationDefaults.GoogleEmailClaim);
                        bool verified = string.Equals(
                            external.Principal?.FindFirstValue(
                                ExternalAuthenticationDefaults
                                    .GoogleEmailVerifiedClaim),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        bool linked = external.Succeeded &&
                            verified &&
                            subject is not null &&
                            email is not null &&
                            await externalUseCase.LinkAsync(
                                result.UserId ?? throw new InvalidOperationException(
                                    "A successful login did not provide a user id."),
                                ExternalAuthenticationDefaults.GoogleProvider,
                                subject,
                                email,
                                cancellationToken);

                        await httpContext.SignOutAsync(
                            ExternalAuthenticationDefaults.CookieScheme);

                        if (!linked)
                        {
                            await revokeSessionUseCase.ExecuteAsync(
                                sessionHandle,
                                cancellationToken);
                            return TypedResults.Problem(
                                title: "Google account link failed",
                                detail: "Não foi possível concluir o vínculo com o Google.",
                                statusCode: StatusCodes.Status409Conflict);
                        }
                    }

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
