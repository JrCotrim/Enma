using Microsoft.AspNetCore.Antiforgery;

namespace Enma.Api.Authentication;

internal static class EnmaAntiforgeryEndpointConvention
{
    internal static RouteHandlerBuilder RequireEnmaAntiforgery(
        this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(
                async (invocationContext, next) =>
                {
                    HttpContext httpContext = invocationContext.HttpContext;
                    IAntiforgeryValidationFeature? validationFeature =
                        httpContext.Features.Get<IAntiforgeryValidationFeature>();

                    if (validationFeature?.IsValid != true)
                    {
                        httpContext.Response.Headers.CacheControl = "no-store";
                        return TypedResults.BadRequest();
                    }

                    return await next(invocationContext);
                });
    }
}
