using Amazon.S3;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Infrastructure;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Documents.Upload;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class DocumentStorageDependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_DocumentStorageSection_BindsAndValidatesExactOptions()
    {
        var values = new Dictionary<string, string?>
        {
            [$"{DocumentStorageOptions.SectionName}:ServiceUrl"] =
                "https://storage.example.test",
            [$"{DocumentStorageOptions.SectionName}:BucketName"] =
                "enma-documents",
            [$"{DocumentStorageOptions.SectionName}:Region"] =
                "us-east-1",
            [$"{DocumentStorageOptions.SectionName}:ForcePathStyle"] =
                "true",
            [$"{DocumentStorageOptions.SectionName}:AccessKey"] =
                "synthetic-access-key",
            [$"{DocumentStorageOptions.SectionName}:SecretKey"] =
                "synthetic-secret-key",
            [$"{DocumentStorageOptions.SectionName}:RequireTls"] =
                "true"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString, configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        DocumentStorageOptions options = serviceProvider
            .GetRequiredService<IOptions<DocumentStorageOptions>>()
            .Value;

        Assert.Equal("https://storage.example.test", options.ServiceUrl);
        Assert.Equal("enma-documents", options.BucketName);
        Assert.Equal("us-east-1", options.Region);
        Assert.True(options.ForcePathStyle);
        Assert.Equal("synthetic-access-key", options.AccessKey);
        Assert.Equal("synthetic-secret-key", options.SecretKey);
        Assert.True(options.RequireTls);
    }

    [Fact]
    public async Task AddInfrastructure_InvalidDocumentStorageSection_FailsSafelyOnResolution()
    {
        const string syntheticSecret = "synthetic-secret-that-must-not-leak";

        var values = new Dictionary<string, string?>
        {
            [$"{DocumentStorageOptions.SectionName}:ServiceUrl"] =
                "http://storage.example.test",
            [$"{DocumentStorageOptions.SectionName}:BucketName"] =
                "ENMA-documents",
            [$"{DocumentStorageOptions.SectionName}:Region"] =
                "us-east-1",
            [$"{DocumentStorageOptions.SectionName}:ForcePathStyle"] =
                "true",
            [$"{DocumentStorageOptions.SectionName}:AccessKey"] =
                "synthetic-access-key",
            [$"{DocumentStorageOptions.SectionName}:SecretKey"] =
                syntheticSecret,
            [$"{DocumentStorageOptions.SectionName}:RequireTls"] =
                "false"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString, configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => serviceProvider
                .GetRequiredService<IOptions<DocumentStorageOptions>>()
                .Value);

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("ServiceUrl", StringComparison.Ordinal)
                || failure.Contains("HTTP storage", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("BucketName", StringComparison.Ordinal));
        Assert.DoesNotContain(
            syntheticSecret,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddInfrastructure_DocumentStorageGraph_UsesSingletonProviderAndAdapter()
    {
        IConfiguration configuration = CreateValidDocumentStorageConfiguration();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(fixture.ConnectionString, configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();

        IAmazonS3 firstClient = firstScope.ServiceProvider
            .GetRequiredService<IAmazonS3>();
        IAmazonS3 secondClient = secondScope.ServiceProvider
            .GetRequiredService<IAmazonS3>();
        ILegalDocumentStorage firstStorage = firstScope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();
        ILegalDocumentStorage secondStorage = secondScope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        Assert.IsType<AmazonS3Client>(firstClient);
        Assert.IsType<S3LegalDocumentStorage>(firstStorage);
        Assert.Same(firstClient, secondClient);
        Assert.Same(firstStorage, secondStorage);
    }

    [Fact]
    public async Task AddInfrastructure_DocumentUploadGraph_UsesScopedCoordinatorAndUseCase()
    {
        IConfiguration configuration = CreateValidDocumentStorageConfiguration();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(fixture.ConnectionString, configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();

        ILegalDocumentMetadataUploadTransaction firstMetadataTransaction = firstScope
            .ServiceProvider
            .GetRequiredService<ILegalDocumentMetadataUploadTransaction>();
        ILegalDocumentUploadPersistence firstUploadPersistence = firstScope
            .ServiceProvider
            .GetRequiredService<ILegalDocumentUploadPersistence>();
        UploadLegalDocumentUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<UploadLegalDocumentUseCase>();
        ILegalDocumentStorage firstStorage = firstScope.ServiceProvider
            .GetRequiredService<ILegalDocumentStorage>();

        Assert.IsType<LegalDocumentMetadataUploadTransaction>(
            firstMetadataTransaction);
        Assert.IsType<LegalDocumentUploadPersistence>(firstUploadPersistence);
        Assert.Same(
            firstMetadataTransaction,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentMetadataUploadTransaction>());
        Assert.Same(
            firstUploadPersistence,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentUploadPersistence>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider
                .GetRequiredService<UploadLegalDocumentUseCase>());

        Assert.NotSame(
            firstMetadataTransaction,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalDocumentMetadataUploadTransaction>());
        Assert.NotSame(
            firstUploadPersistence,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalDocumentUploadPersistence>());
        Assert.NotSame(
            firstUseCase,
            secondScope.ServiceProvider
                .GetRequiredService<UploadLegalDocumentUseCase>());
        Assert.Same(
            firstStorage,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalDocumentStorage>());
    }

    private static IConfiguration CreateValidDocumentStorageConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            [$"{DocumentStorageOptions.SectionName}:ServiceUrl"] =
                "https://storage.example.test",
            [$"{DocumentStorageOptions.SectionName}:BucketName"] =
                "enma-documents",
            [$"{DocumentStorageOptions.SectionName}:Region"] =
                "us-east-1",
            [$"{DocumentStorageOptions.SectionName}:ForcePathStyle"] =
                "true",
            [$"{DocumentStorageOptions.SectionName}:AccessKey"] =
                "synthetic-access-key",
            [$"{DocumentStorageOptions.SectionName}:SecretKey"] =
                "synthetic-secret-key",
            [$"{DocumentStorageOptions.SectionName}:RequireTls"] =
                "true"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}