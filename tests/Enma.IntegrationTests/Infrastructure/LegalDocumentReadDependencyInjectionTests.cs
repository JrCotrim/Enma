using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.Download;
using Enma.Application.Documents.GetById;
using Enma.Application.Documents.List;
using Enma.Infrastructure;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentReadDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_DocumentReadGraph_UsesScopedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(
            fixture.ConnectionString,
            CreateValidDocumentStorageConfiguration());

        await using ServiceProvider serviceProvider =
            services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });
        await using AsyncServiceScope firstScope =
            serviceProvider.CreateAsyncScope();
        ILegalDocumentReadQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<ILegalDocumentReadQueries>();
        ILegalDocumentContentReadQueries firstContentQueries =
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentContentReadQueries>();
        LegalDocumentReadAuthorization firstAuthorization =
            firstScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>();
        GetLegalDocumentUseCase firstGet = firstScope.ServiceProvider
            .GetRequiredService<GetLegalDocumentUseCase>();
        DownloadLegalDocumentUseCase firstDownload =
            firstScope.ServiceProvider
                .GetRequiredService<DownloadLegalDocumentUseCase>();
        ListLegalDocumentsUseCase firstList = firstScope.ServiceProvider
            .GetRequiredService<ListLegalDocumentsUseCase>();

        Assert.IsType<LegalDocumentReadQueries>(firstQueries);
        Assert.IsType<LegalDocumentContentReadQueries>(firstContentQueries);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentReadQueries>());
        Assert.Same(
            firstContentQueries,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentContentReadQueries>());
        Assert.Same(
            firstAuthorization,
            firstScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>());
        Assert.Same(
            firstGet,
            firstScope.ServiceProvider
                .GetRequiredService<GetLegalDocumentUseCase>());
        Assert.Same(
            firstDownload,
            firstScope.ServiceProvider
                .GetRequiredService<DownloadLegalDocumentUseCase>());
        Assert.Same(
            firstList,
            firstScope.ServiceProvider
                .GetRequiredService<ListLegalDocumentsUseCase>());

        await using AsyncServiceScope secondScope =
            serviceProvider.CreateAsyncScope();

        Assert.NotSame(
            firstQueries,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalDocumentReadQueries>());
        Assert.NotSame(
            firstContentQueries,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalDocumentContentReadQueries>());
        Assert.NotSame(
            firstAuthorization,
            secondScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>());
        Assert.NotSame(
            firstGet,
            secondScope.ServiceProvider
                .GetRequiredService<GetLegalDocumentUseCase>());
        Assert.NotSame(
            firstDownload,
            secondScope.ServiceProvider
                .GetRequiredService<DownloadLegalDocumentUseCase>());
        Assert.NotSame(
            firstList,
            secondScope.ServiceProvider
                .GetRequiredService<ListLegalDocumentsUseCase>());
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
