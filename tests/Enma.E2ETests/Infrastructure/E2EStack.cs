using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Enma.E2ETests.Infrastructure;

// One disposable stack per run, shared by every journey class: PostgreSQL
// (migrated once, then given the D33 roles), Mailpit and MinIO containers, the
// checked Enma.Web build and a headless Chromium. Each journey class gets its
// own API host on top of it (E2EHost), connected as the application role.
// Nothing here touches the Development or Pilot stores.
public sealed class E2EStack : IAsyncLifetime
{
    private const string MigratorRole = "enma_e2e_migrator";
    private const string AppRole = "enma_e2e_app";
    private const string PostgreSqlImage = "postgres:18-alpine";
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
    private const string MinioImage = "docker.io/pgsty/silo:RELEASE.2026-09-16T00-00-00Z@sha256:635197cb9f36d01bee221d34d1c7d7960f6a95c48b0b6c01d99cd13bdae51a46";
    private const ushort MailpitSmtpPort = 1025;
    private const ushort MailpitApiPort = 8025;
    private const ushort MinioApiPort = 9000;
    private const string DocumentBucketName = "enma-e2e-documents";
    private const string StorageRegion = "us-east-1";

    private readonly string storageAccessKey = $"e2e-{CreateSecret(8)}";
    private readonly string storageSecretKey = CreateSecret(24);
    private readonly string migratorPassword = CreateSecret(24);
    private readonly string appPassword = CreateSecret(24);
    private readonly PostgreSqlContainer postgreSql;
    private readonly IContainer mailpit;
    private readonly IContainer minio;

    private IPlaywright? playwright;
    private IBrowser? browser;
    private MailpitClient? mailpitClient;
    private string? distributionPath;
    private string? storageServiceUrl;

    public E2EStack()
    {
        postgreSql = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("enma_e2e")
            .WithUsername("enma_e2e")
            .WithPassword(CreateSecret(24))
            .WithCreateParameterModifier(LoopbackPortBindings.Apply)
            .Build();
        mailpit = new ContainerBuilder(MailpitImage)
            .WithPortBinding(MailpitSmtpPort, true)
            .WithPortBinding(MailpitApiPort, true)
            .WithCreateParameterModifier(LoopbackPortBindings.Apply)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(MailpitApiPort)
                    .ForPath("/api/v1/messages")))
            .Build();
        minio = new ContainerBuilder(MinioImage)
            .WithCommand("server", "/data")
            .WithEnvironment("MINIO_ROOT_USER", storageAccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", storageSecretKey)
            .WithPortBinding(MinioApiPort, true)
            .WithCreateParameterModifier(LoopbackPortBindings.Apply)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(MinioApiPort)
                    .ForPath("/minio/health/ready")))
            .Build();
    }

    public string RepositoryRoot { get; } = RepositoryPaths.FindRoot();

    public string DistributionPath => distributionPath ?? throw NotStarted();

    public IBrowser Browser => browser ?? throw NotStarted();

    public MailpitClient Mailpit => mailpitClient ?? throw NotStarted();

    public async Task InitializeAsync()
    {
        distributionPath = WebDistribution.EnsureCurrent(RepositoryRoot);

        using var startupTimeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(3));
        await Task.WhenAll(
            postgreSql.StartAsync(startupTimeout.Token),
            mailpit.StartAsync(startupTimeout.Token),
            minio.StartAsync(startupTimeout.Token));

        await MigrateDatabaseAsync();
        storageServiceUrl =
            $"http://127.0.0.1:{minio.GetMappedPublicPort(MinioApiPort)}";
        await CreateDocumentBucketAsync(storageServiceUrl);

        mailpitClient = new MailpitClient(new Uri(
            $"http://127.0.0.1:{mailpit.GetMappedPublicPort(MailpitApiPort)}/"));

        Assertions.SetDefaultExpectTimeout(10_000);
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
        mailpitClient?.Dispose();

        await Task.WhenAll(
            postgreSql.DisposeAsync().AsTask(),
            mailpit.DisposeAsync().AsTask(),
            minio.DisposeAsync().AsTask());
    }

    // Settings for one API host served at baseAddress. All hosts share the
    // same database, mail sink and bucket; e-mail links point at the host that
    // sent them.
    public Dictionary<string, string?> CreateApplicationSettings(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        string delivery = "EmailVerification:Delivery";

        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = new NpgsqlConnectionStringBuilder(
                postgreSql.GetConnectionString())
            {
                Username = AppRole,
                Password = appPassword
            }.ConnectionString,
            [$"{delivery}:VerificationPageUrl"] =
                new Uri(baseAddress, "verify-email").AbsoluteUri,
            [$"{delivery}:PasswordRecoveryPageUrl"] =
                new Uri(baseAddress, "reset-password").AbsoluteUri,
            [$"{delivery}:SenderName"] = "ENMA E2E",
            [$"{delivery}:SenderAddress"] = "no-reply@enma-e2e.test",
            [$"{delivery}:SmtpHost"] = "127.0.0.1",
            [$"{delivery}:SmtpPort"] = mailpit
                .GetMappedPublicPort(MailpitSmtpPort)
                .ToString(),
            [$"{delivery}:SmtpSecurity"] = "None",
            [$"{delivery}:SmtpUsername"] = string.Empty,
            [$"{delivery}:SmtpPassword"] = string.Empty,
            ["DocumentStorage:ServiceUrl"] = storageServiceUrl ?? throw NotStarted(),
            ["DocumentStorage:BucketName"] = DocumentBucketName,
            ["DocumentStorage:Region"] = StorageRegion,
            ["DocumentStorage:ForcePathStyle"] = "true",
            ["DocumentStorage:RequireTls"] = "false",
            ["DocumentStorage:AccessKey"] = storageAccessKey,
            ["DocumentStorage:SecretKey"] = storageSecretKey,
            ["Logging:LogLevel:Default"] = "Warning"
        };
    }

    private async Task MigrateDatabaseAsync()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(postgreSql.GetConnectionString())
                .Options;

        await using (var dbContext = new EnmaDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        await PostgreSqlRoles.ApplyAsync(
            postgreSql.GetConnectionString(),
            MigratorRole,
            migratorPassword,
            AppRole,
            appPassword);
    }

    private async Task CreateDocumentBucketAsync(string storageServiceUrl)
    {
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(storageAccessKey, storageSecretKey),
            new AmazonS3Config
            {
                ServiceURL = storageServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = StorageRegion
            });

        await client.PutBucketAsync(DocumentBucketName);
    }

    private static string CreateSecret(int bytes)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    }

    private static InvalidOperationException NotStarted()
    {
        return new InvalidOperationException("The E2E stack has not started.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ECollection : ICollectionFixture<E2EStack>
{
    public const string Name = "E2E stack";
}
