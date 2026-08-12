namespace Enma.Api.Endpoints.Clients;

internal static class ClientResponseCachePolicy
{
    internal static RouteGroupBuilder RequireClientNoStoreResponses(
        this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.WithMetadata(ClientNoStoreMetadata.Instance);
    }

    internal static IApplicationBuilder UseClientNoStoreResponses(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return application.Use(
            async (httpContext, next) =>
            {
                if (httpContext.GetEndpoint()?.Metadata
                    .GetMetadata<ClientNoStoreMetadata>() is not null)
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                }

                await next(httpContext);
            });
    }

    private sealed class ClientNoStoreMetadata
    {
        internal static ClientNoStoreMetadata Instance { get; } = new();
    }
}
