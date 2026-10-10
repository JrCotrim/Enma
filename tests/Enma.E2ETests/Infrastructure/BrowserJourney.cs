using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Enma.E2ETests.Infrastructure;

// Runs a journey in a fresh browser context. Tracing always records, but the
// trace and a screenshot are kept only when the journey fails, under
// artifacts/e2e/<class>.<method>/. The class is taken from the calling file,
// which follows the one-class-per-file convention of Journeys/.
public static class BrowserJourney
{
    public static async Task RunAsync(
        E2EHost host,
        Func<IPage, Task> journey,
        BrowserNewContextOptions? contextOptions = null,
        [CallerMemberName] string testName = "",
        [CallerFilePath] string testFile = "")
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(journey);

        string artifactsDirectory = Path.Combine(
            host.RepositoryRoot,
            "artifacts",
            "e2e",
            $"{Path.GetFileNameWithoutExtension(testFile)}.{testName}");
        if (Directory.Exists(artifactsDirectory))
        {
            Directory.Delete(artifactsDirectory, recursive: true);
        }

        IBrowserContext context = await NewContextAsync(host, contextOptions);
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Title = testName,
            Screenshots = true,
            Snapshots = true
        });
        IPage page = await context.NewPageAsync();
        bool succeeded = false;

        try
        {
            await journey(page);
            succeeded = true;
        }
        finally
        {
            if (succeeded)
            {
                await context.Tracing.StopAsync();
            }
            else
            {
                await SaveFailureArtifactsAsync(context, page, artifactsDirectory);
            }

            await context.CloseAsync();
        }
    }

    // A further browser context on the same host, for journeys that need a
    // second person's session next to the main one. It is not traced; the
    // caller disposes it.
    public static async Task<IBrowserContext> NewContextAsync(
        E2EHost host,
        BrowserNewContextOptions? contextOptions = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        BrowserNewContextOptions options = contextOptions ?? new();
        options.BaseURL = host.BaseAddress.AbsoluteUri;
        options.IgnoreHTTPSErrors = true;
        options.Locale = "pt-BR";
        options.TimezoneId = "America/Sao_Paulo";

        IBrowserContext context = await host.Browser.NewContextAsync(options);
        context.SetDefaultTimeout(15_000);
        return context;
    }

    // Best effort: never mask the journey's own failure.
    private static async Task SaveFailureArtifactsAsync(
        IBrowserContext context,
        IPage page,
        string directory)
    {
        Directory.CreateDirectory(directory);

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(directory, "failure.png"),
                FullPage = true
            });
        }
        catch (PlaywrightException)
        {
        }

        try
        {
            await context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(directory, "trace.zip")
            });
        }
        catch (PlaywrightException)
        {
        }
    }
}
