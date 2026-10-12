using Microsoft.Extensions.FileProviders;

namespace Enma.Api.Deployment;

// Serves the Enma.Web build from the web root on the API origin (D3). Outside
// Development the web root is served whenever it holds index.html; Development
// keeps the Vite dev server and never serves it. Static files run early; the
// SPA fallback runs only after the authentication handlers and only when no
// endpoint matched, and never answers /api or /health paths, which keep the
// API's own empty 404.
internal static class WebDistributionHosting
{
    private const string IndexFileName = "index.html";
    private const string ImmutableCacheControl =
        "public, max-age=31536000, immutable";
    private const string IndexCacheControl = "no-cache";

    public static IFileProvider? Resolve(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment() ||
            !environment.WebRootFileProvider.GetFileInfo(IndexFileName).Exists)
        {
            return null;
        }

        return environment.WebRootFileProvider;
    }

    public static IApplicationBuilder UseWebDistributionFiles(
        this IApplicationBuilder application,
        IFileProvider fileProvider)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(fileProvider);

        return application.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = context =>
            {
                if (IsIndex(context.File))
                {
                    context.Context.Response.Headers.CacheControl =
                        IndexCacheControl;
                }
                else if (context.Context.Request.Path
                    .StartsWithSegments("/assets"))
                {
                    // Vite fingerprints every file it emits under /assets.
                    context.Context.Response.Headers.CacheControl =
                        ImmutableCacheControl;
                }
            }
        });
    }

    public static IApplicationBuilder UseWebDistributionFallback(
        this IApplicationBuilder application,
        IFileProvider fileProvider)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(fileProvider);

        return application.Use((context, next) =>
            context.GetEndpoint() is null && IsSpaNavigation(context.Request)
                ? ServeIndexAsync(context, fileProvider)
                : next(context));
    }

    private static bool IsSpaNavigation(HttpRequest request)
    {
        return (HttpMethods.IsGet(request.Method) ||
                HttpMethods.IsHead(request.Method)) &&
            !request.Path.StartsWithSegments("/api") &&
            !request.Path.StartsWithSegments("/health") &&
            !Path.HasExtension(request.Path.Value);
    }

    private static bool IsIndex(IFileInfo file)
    {
        return string.Equals(file.Name, IndexFileName, StringComparison.Ordinal);
    }

    private static Task ServeIndexAsync(
        HttpContext context,
        IFileProvider fileProvider)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = IndexCacheControl;
        return context.Response.SendFileAsync(
            fileProvider.GetFileInfo(IndexFileName),
            context.RequestAborted);
    }
}
