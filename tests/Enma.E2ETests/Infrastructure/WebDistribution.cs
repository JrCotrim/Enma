namespace Enma.E2ETests.Infrastructure;

// The journeys exercise the production build of Enma.Web. A missing or stale
// dist would silently test old UI, so the stack refuses to start instead.
internal static class WebDistribution
{
    public static string EnsureCurrent(string repositoryRoot)
    {
        string webRoot = Path.Combine(repositoryRoot, "src", "Enma.Web");
        string distributionPath = Path.Combine(webRoot, "dist");
        string indexPath = Path.Combine(distributionPath, "index.html");

        if (!File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                "src/Enma.Web/dist/index.html was not found. " +
                "Run `npm run build` in src/Enma.Web before the E2E tests.");
        }

        DateTime builtAt = File.GetLastWriteTimeUtc(indexPath);
        string newestSource = Directory
            .EnumerateFiles(
                Path.Combine(webRoot, "src"),
                "*",
                SearchOption.AllDirectories)
            .Append(Path.Combine(webRoot, "index.html"))
            .Append(Path.Combine(webRoot, "vite.config.ts"))
            .MaxBy(File.GetLastWriteTimeUtc)!;

        if (File.GetLastWriteTimeUtc(newestSource) > builtAt)
        {
            throw new InvalidOperationException(
                "src/Enma.Web/dist is older than " +
                $"{Path.GetRelativePath(webRoot, newestSource)}. " +
                "Run `npm run build` in src/Enma.Web before the E2E tests.");
        }

        return distributionPath;
    }
}
