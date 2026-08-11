using Enma.Api.Authentication;
using Enma.Application.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Enma.Api.Endpoints.Authentication;

public static class LogoutEndpoints
{
    public static IEndpointRouteBuilder MapLogoutEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost(
                "/api/auth/logout",
                async Task<NoContent> (
                    RevokeSessionUseCase useCase,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    httpContext.Request.Cookies.TryGetValue(
                        AuthenticationCookies.SessionName,
                        out string? rawHandle);

                    await useCase.ExecuteAsync(rawHandle, cancellationToken);

                    httpContext.Response.Cookies.Delete(
                        AuthenticationCookies.SessionName,
                        AuthenticationCookies.CreateSessionOptions());
                    httpContext.Response.Cookies.Delete(
                        AuthenticationCookies.AntiforgeryName,
                        AuthenticationCookies.CreateAntiforgeryOptions());
                    httpContext.Response.Headers.CacheControl = "no-store";

                    return TypedResults.NoContent();
                })
            .WithName("Logout")
            .WithSummary("Revokes the presented authentication session.")
            .WithTags("Authentication")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }
}
