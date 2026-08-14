using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.GetById;
using Enma.Application.Tasks.List;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskReadDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_LegalTaskReadGraph_UsesScopedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(
            fixture.ConnectionString,
            new ConfigurationBuilder().Build());

        await using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();
        ILegalTaskReadQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<ILegalTaskReadQueries>();
        LegalTaskViewAuthorization firstAuthorization = firstScope.ServiceProvider
            .GetRequiredService<LegalTaskViewAuthorization>();
        GetLegalTaskUseCase firstGet = firstScope.ServiceProvider
            .GetRequiredService<GetLegalTaskUseCase>();
        ListLegalTasksUseCase firstList = firstScope.ServiceProvider
            .GetRequiredService<ListLegalTasksUseCase>();

        Assert.IsType<LegalTaskReadQueries>(firstQueries);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider.GetRequiredService<ILegalTaskReadQueries>());
        Assert.Same(
            firstAuthorization,
            firstScope.ServiceProvider
                .GetRequiredService<LegalTaskViewAuthorization>());
        Assert.Same(
            firstGet,
            firstScope.ServiceProvider.GetRequiredService<GetLegalTaskUseCase>());
        Assert.Same(
            firstList,
            firstScope.ServiceProvider.GetRequiredService<ListLegalTasksUseCase>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();

        Assert.NotSame(
            firstQueries,
            secondScope.ServiceProvider.GetRequiredService<ILegalTaskReadQueries>());
        Assert.NotSame(
            firstAuthorization,
            secondScope.ServiceProvider
                .GetRequiredService<LegalTaskViewAuthorization>());
        Assert.NotSame(
            firstGet,
            secondScope.ServiceProvider.GetRequiredService<GetLegalTaskUseCase>());
        Assert.NotSame(
            firstList,
            secondScope.ServiceProvider.GetRequiredService<ListLegalTasksUseCase>());
    }
}
