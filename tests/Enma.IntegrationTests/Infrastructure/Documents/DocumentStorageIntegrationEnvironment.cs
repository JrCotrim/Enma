namespace Enma.IntegrationTests.Infrastructure.Documents;

internal sealed class DocumentStorageIntegrationEnvironment
{
    private static readonly string[] RequiredKeys =
    [
        "MINIO_APP_ACCESS_KEY",
        "MINIO_APP_SECRET_KEY",
        "MINIO_API_PORT"
    ];

    private DocumentStorageIntegrationEnvironment(
        string serviceUrl,
        string appAccessKey,
        string appSecretKey)
    {
        ServiceUrl = serviceUrl;
        AppAccessKey = appAccessKey;
        AppSecretKey = appSecretKey;
    }

    public const string BucketName = "enma-documents";

    public const string Region = "us-east-1";

    public string ServiceUrl { get; }

    public string AppAccessKey { get; }

    public string AppSecretKey { get; }

    public static DocumentStorageIntegrationEnvironment Load()
    {
        string repositoryRoot = FindRepositoryRoot();
        string localEnvironmentPath = Path.Combine(repositoryRoot, ".env");
        string exampleEnvironmentPath = Path.Combine(repositoryRoot, ".env.example");
        string environmentPath = File.Exists(localEnvironmentPath)
            ? localEnvironmentPath
            : exampleEnvironmentPath;

        if (!File.Exists(environmentPath))
        {
            throw new InvalidOperationException(
                "Document storage integration tests require .env or .env.example.");
        }

        IReadOnlyDictionary<string, string> settings =
            ReadEnvironment(environmentPath);

        if (!int.TryParse(
                settings["MINIO_API_PORT"],
                out int minioApiPort)
            || minioApiPort is < 1 or > 65_535)
        {
            throw new InvalidOperationException(
                "MINIO_API_PORT must be a valid TCP port.");
        }

        return new DocumentStorageIntegrationEnvironment(
            $"http://127.0.0.1:{minioApiPort}",
            settings["MINIO_APP_ACCESS_KEY"],
            settings["MINIO_APP_SECRET_KEY"]);
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(
        string path)
    {
        var settings = new Dictionary<string, string>(
            StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            string trimmedLine = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmedLine)
                || trimmedLine.StartsWith(
                    "#",
                    StringComparison.Ordinal))
            {
                continue;
            }

            int separatorIndex = line.IndexOf(
                '=',
                StringComparison.Ordinal);

            if (separatorIndex < 1)
            {
                throw new InvalidOperationException(
                    $"Invalid setting on line {lineNumber} of the local environment file.");
            }

            string key = line[..separatorIndex].Trim();

            if (!RequiredKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (!settings.TryAdd(
                    key,
                    line[(separatorIndex + 1)..].Trim()))
            {
                throw new InvalidOperationException(
                    $"Duplicate required setting '{key}' in the local environment file.");
            }
        }

        foreach (string requiredKey in RequiredKeys)
        {
            if (!settings.TryGetValue(
                    requiredKey,
                    out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Required setting '{requiredKey}' is missing from the local environment file.");
            }
        }

        return settings;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "Enma.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Unable to locate the ENMA repository root.");
    }
}
