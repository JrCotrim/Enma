using Microsoft.AspNetCore.Antiforgery;

namespace Enma.Api.Endpoints.Authentication;

public static class CsrfEndpoints
{
    public static IEndpointRouteBuilder MapCsrfEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(
                "/api/auth/csrf",
                (IAntiforgery antiforgery, HttpContext httpContext) =>
                {
                    AntiforgeryTokenSet tokens =
                        antiforgery.GetAndStoreTokens(httpContext);
                    string requestToken = tokens.RequestToken
                        ?? throw new InvalidOperationException(
                            "Antiforgery token generation did not provide a request token.");

                    httpContext.Response.Headers.CacheControl = "no-store";
                    return TypedResults.Ok(new CsrfResponse(requestToken));
                })
            .WithName("GetCsrfToken")
            .WithSummary("Creates an antiforgery request token.")
            .WithTags("Authentication")
            .Produces<CsrfResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private sealed record CsrfResponse(string RequestToken);
}
