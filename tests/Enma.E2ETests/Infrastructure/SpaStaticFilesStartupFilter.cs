using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Enma.E2ETests.Infrastructure;

// Test-only stand-in for the production edge: serves the Enma.Web build on the
// API origin with SPA fallback. Static files run before the API pipeline; the
// fallback runs only after no API endpoint matched, and never answers /api
// paths, which keep the API's own 404.
internal sealed class SpaStaticFilesStartupFilter : IStartupFilter
{
    private readonly string distributionPath;

    public SpaStaticFilesStartupFilter(string distributionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionPath);
        this.distributionPath = distributionPath;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            var fileProvider = new PhysicalFileProvider(distributionPath);

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider
            });
            next(app);
            app.Run(context => IsSpaNavigation(context.Request)
                ? ServeIndexAsync(context, fileProvider)
                : RespondNotFoundAsync(context));
        };
    }

    private static bool IsSpaNavigation(HttpRequest request)
    {
        return (HttpMethods.IsGet(request.Method) ||
                HttpMethods.IsHead(request.Method)) &&
            !request.Path.StartsWithSegments("/api") &&
            !Path.HasExtension(request.Path.Value);
    }

    private static Task ServeIndexAsync(
        HttpContext context,
        IFileProvider fileProvider)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.SendFileAsync(
            fileProvider.GetFileInfo("index.html"),
            context.RequestAborted);
    }

    private static Task RespondNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
