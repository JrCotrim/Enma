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

                    if (validationFeature is null)
                    {
                        try
                        {
                            IAntiforgery antiforgery = httpContext.RequestServices
                                .GetRequiredService<IAntiforgery>();
                            await antiforgery.ValidateRequestAsync(httpContext);
                        }
                        catch (AntiforgeryValidationException)
                        {
                            httpContext.Response.Headers.CacheControl = "no-store";
                            return TypedResults.BadRequest();
                        }
                    }
                    else if (!validationFeature.IsValid)
                    {
                        httpContext.Response.Headers.CacheControl = "no-store";
                        return TypedResults.BadRequest();
                    }

                    return await next(invocationContext);
                });
    }
}
