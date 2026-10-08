using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace Enma.E2ETests.Infrastructure;

// One disposable stack per run: PostgreSQL, Mailpit and MinIO containers, the
// API with the Enma.Web build on one HTTPS origin, and a headless Chromium.
// Nothing here touches the Development or Pilot stores.
public sealed class E2EStack : IAsyncLifetime
{
    private const string PostgreSqlImage = "postgres:18-alpine";
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
    private const string MinioImage = "minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const ushort MailpitSmtpPort = 1025;
    private const ushort MailpitApiPort = 8025;
    private const ushort MinioApiPort = 9000;
    private const string DocumentBucketName = "enma-e2e-documents";
    private const string StorageRegion = "us-east-1";

    private readonly string storageAccessKey = $"e2e-{CreateSecret(8)}";
    private readonly string storageSecretKey = CreateSecret(24);
    private readonly PostgreSqlContainer postgreSql;
    private readonly IContainer mailpit;
    private readonly IContainer minio;

    private EnmaE2EApplicationFactory? application;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private MailpitClient? mailpitClient;
    private Uri? baseAddress;

    public E2EStack()
    {
        postgreSql = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("enma_e2e")
            .WithUsername("enma_e2e")
            .WithPassword(CreateSecret(24))
            .Build();
        mailpit = new ContainerBuilder(MailpitImage)
            .WithPortBinding(MailpitSmtpPort, true)
            .WithPortBinding(MailpitApiPort, true)
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
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(MinioApiPort)
                    .ForPath("/minio/health/ready")))
            .Build();
    }

    public string RepositoryRoot { get; } = RepositoryPaths.FindRoot();

    public Uri BaseAddress => baseAddress ?? throw NotStarted();

    public IBrowser Browser => browser ?? throw NotStarted();

    public MailpitClient Mailpit => mailpitClient ?? throw NotStarted();

    public ApiSeeder Seeder => new(BaseAddress, Mailpit);

    public async Task InitializeAsync()
    {
        string distributionPath = WebDistribution.EnsureCurrent(RepositoryRoot);

        using var startupTimeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(3));
        await Task.WhenAll(
            postgreSql.StartAsync(startupTimeout.Token),
            mailpit.StartAsync(startupTimeout.Token),
            minio.StartAsync(startupTimeout.Token));

        await MigrateDatabaseAsync();
        string storageServiceUrl =
            $"http://127.0.0.1:{minio.GetMappedPublicPort(MinioApiPort)}";
        await CreateDocumentBucketAsync(storageServiceUrl);

        int port = FindFreeLoopbackPort();
        baseAddress = new Uri($"https://localhost:{port}/");
        application = new EnmaE2EApplicationFactory(
            CreateApplicationSettings(storageServiceUrl),
            distributionPath);
        application.UseKestrel(options =>
            options.ListenLocalhost(port, listen => listen.UseHttps()));
        application.StartServer();

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

        if (application is not null)
        {
            await application.DisposeAsync();
        }

        await Task.WhenAll(
            postgreSql.DisposeAsync().AsTask(),
            mailpit.DisposeAsync().AsTask(),
            minio.DisposeAsync().AsTask());
    }

    private Dictionary<string, string?> CreateApplicationSettings(
        string storageServiceUrl)
    {
        string delivery = "EmailVerification:Delivery";

        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = postgreSql.GetConnectionString(),
            [$"{delivery}:VerificationPageUrl"] =
                new Uri(BaseAddress, "verify-email").AbsoluteUri,
            [$"{delivery}:PasswordRecoveryPageUrl"] =
                new Uri(BaseAddress, "reset-password").AbsoluteUri,
            [$"{delivery}:SenderName"] = "ENMA E2E",
            [$"{delivery}:SenderAddress"] = "no-reply@enma-e2e.test",
            [$"{delivery}:SmtpHost"] = "127.0.0.1",
            [$"{delivery}:SmtpPort"] = mailpit
                .GetMappedPublicPort(MailpitSmtpPort)
                .ToString(),
            [$"{delivery}:SmtpSecurity"] = "None",
            [$"{delivery}:SmtpUsername"] = string.Empty,
            [$"{delivery}:SmtpPassword"] = string.Empty,
            ["DocumentStorage:ServiceUrl"] = storageServiceUrl,
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

        await using var dbContext = new EnmaDbContext(options);
        await dbContext.Database.MigrateAsync();
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

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
