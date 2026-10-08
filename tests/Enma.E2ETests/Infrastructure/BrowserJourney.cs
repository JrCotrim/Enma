using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Enma.E2ETests.Infrastructure;

// Runs a journey in a fresh browser context. Tracing always records, but the
// trace and a screenshot are kept only when the journey fails, under
// artifacts/e2e/<test name>/.
public static class BrowserJourney
{
    public static async Task RunAsync(
        E2EStack stack,
        Func<IPage, Task> journey,
        BrowserNewContextOptions? contextOptions = null,
        [CallerMemberName] string testName = "")
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(journey);

        string artifactsDirectory = Path.Combine(
            stack.RepositoryRoot,
            "artifacts",
            "e2e",
            testName);
        if (Directory.Exists(artifactsDirectory))
        {
            Directory.Delete(artifactsDirectory, recursive: true);
        }

        BrowserNewContextOptions options = contextOptions ?? new();
        options.BaseURL = stack.BaseAddress.AbsoluteUri;
        options.IgnoreHTTPSErrors = true;
        options.Locale = "pt-BR";
        options.TimezoneId = "America/Sao_Paulo";

        IBrowserContext context = await stack.Browser.NewContextAsync(options);
        context.SetDefaultTimeout(15_000);
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
