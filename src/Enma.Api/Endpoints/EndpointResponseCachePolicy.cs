namespace Enma.Api.Endpoints;

internal static class EndpointResponseCachePolicy
{
    internal static RouteGroupBuilder RequireNoStoreResponses(
        this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.WithMetadata(NoStoreMetadata.Instance);
    }

    internal static IApplicationBuilder UseNoStoreResponses(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return application.Use(
            async (httpContext, next) =>
            {
                if (httpContext.GetEndpoint()?.Metadata
                    .GetMetadata<NoStoreMetadata>() is not null)
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                }

                await next(httpContext);
            });
    }

    private sealed class NoStoreMetadata
    {
        internal static NoStoreMetadata Instance { get; } = new();
    }
}
