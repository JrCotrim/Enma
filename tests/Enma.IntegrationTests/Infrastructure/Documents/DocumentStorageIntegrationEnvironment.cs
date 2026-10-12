using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Enma.IntegrationTests.Infrastructure.Documents;

// One disposable MinIO per test run, shared by every document storage test
// (same image as the E2E stack). The fixture creates the private bucket and an
// application identity restricted by the repository's bucket policy, as the
// compose bootstrap does. Credentials are generated here; nothing is read from
// .env and the Development MinIO is never touched. The Testcontainers resource
// reaper removes the container when the test process exits.
internal sealed class DocumentStorageIntegrationEnvironment
{
    private const string MinioImage = "minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const ushort MinioApiPort = 9000;
    private const string AdminAlias = "enma";
    private const string PolicyName = "enma-documents-app";
    private const string PolicyContainerPath =
        "/policies/enma-documents-app-policy.json";

    private static readonly Lazy<Task<DocumentStorageIntegrationEnvironment>>
        SharedEnvironment = new(
            // Off the xUnit synchronization context: Load() blocks on it from
            // synchronous test constructors.
            () => Task.Run(StartAsync),
            LazyThreadSafetyMode.ExecutionAndPublication);

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
        return SharedEnvironment.Value.GetAwaiter().GetResult();
    }

    private static async Task<DocumentStorageIntegrationEnvironment> StartAsync()
    {
        string rootUser = $"root-{CreateSecret(8)}";
        string rootPassword = CreateSecret(24);
        string appAccessKey = $"app-{CreateSecret(8)}";
        string appSecretKey = CreateSecret(24);
        byte[] policy = await File.ReadAllBytesAsync(Path.Combine(
            FindRepositoryRoot(),
            "infrastructure",
            "minio",
            "enma-documents-app-policy.json"));

        IContainer minio = new ContainerBuilder(MinioImage)
            .WithCommand("server", "/data")
            .WithEnvironment("MINIO_ROOT_USER", rootUser)
            .WithEnvironment("MINIO_ROOT_PASSWORD", rootPassword)
            .WithEnvironment(
                $"MC_HOST_{AdminAlias}",
                $"http://{rootUser}:{rootPassword}@127.0.0.1:{MinioApiPort}")
            .WithResourceMapping(policy, PolicyContainerPath)
            .WithPortBinding(MinioApiPort, true)
            .WithCreateParameterModifier(LoopbackPortBindings.Apply)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(MinioApiPort)
                    .ForPath("/minio/health/ready")))
            .Build();

        using var startupTimeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(3));
        await minio.StartAsync(startupTimeout.Token);

        string bucket = $"{AdminAlias}/{BucketName}";
        await RunAdminCommandAsync(minio, ["mb", bucket]);
        await RunAdminCommandAsync(minio, ["anonymous", "set", "none", bucket]);
        await RunAdminCommandAsync(
            minio,
            ["admin", "policy", "create", AdminAlias, PolicyName, PolicyContainerPath]);
        await RunAdminCommandAsync(
            minio,
            ["admin", "user", "add", AdminAlias, appAccessKey, appSecretKey]);
        await RunAdminCommandAsync(
            minio,
            ["admin", "policy", "attach", AdminAlias, PolicyName, "--user", appAccessKey]);

        return new DocumentStorageIntegrationEnvironment(
            $"http://127.0.0.1:{minio.GetMappedPublicPort(MinioApiPort)}",
            appAccessKey,
            appSecretKey);
    }

    private static async Task RunAdminCommandAsync(
        IContainer minio,
        string[] arguments)
    {
        ExecResult result = await minio.ExecAsync(["mc", .. arguments]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MinIO bootstrap step 'mc {arguments[0]} {arguments[1]}' failed " +
                $"with exit code {result.ExitCode}.");
        }
    }

    private static string CreateSecret(int bytes)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
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
