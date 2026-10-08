namespace Enma.E2ETests.Infrastructure;

internal static class RepositoryPaths
{
    public static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Enma.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Unable to locate the ENMA repository root.");
    }
}
