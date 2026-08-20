using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.GetById;
using Enma.Application.Documents.List;
using Enma.Infrastructure;
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
            new ConfigurationBuilder().Build());

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
        LegalDocumentReadAuthorization firstAuthorization =
            firstScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>();
        GetLegalDocumentUseCase firstGet = firstScope.ServiceProvider
            .GetRequiredService<GetLegalDocumentUseCase>();
        ListLegalDocumentsUseCase firstList = firstScope.ServiceProvider
            .GetRequiredService<ListLegalDocumentsUseCase>();

        Assert.IsType<LegalDocumentReadQueries>(firstQueries);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalDocumentReadQueries>());
        Assert.Same(
            firstAuthorization,
            firstScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>());
        Assert.Same(
            firstGet,
            firstScope.ServiceProvider
                .GetRequiredService<GetLegalDocumentUseCase>());
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
            firstAuthorization,
            secondScope.ServiceProvider
                .GetRequiredService<LegalDocumentReadAuthorization>());
        Assert.NotSame(
            firstGet,
            secondScope.ServiceProvider
                .GetRequiredService<GetLegalDocumentUseCase>());
        Assert.NotSame(
            firstList,
            secondScope.ServiceProvider
                .GetRequiredService<ListLegalDocumentsUseCase>());
    }
}
