using Enma.Application.Dashboard;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class DashboardDependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_DashboardReadGraph_IsScopedAndComplete()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(
            fixture.ConnectionString,
            new ConfigurationBuilder().Build());

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();

        IDashboardReadQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<IDashboardReadQueries>();
        GetDashboardUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<GetDashboardUseCase>();

        Assert.IsType<DashboardReadQueries>(firstQueries);
        Assert.NotNull(firstUseCase);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider.GetRequiredService<IDashboardReadQueries>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider.GetRequiredService<GetDashboardUseCase>());

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        Assert.NotSame(
            firstQueries,
            secondScope.ServiceProvider.GetRequiredService<IDashboardReadQueries>());
        Assert.NotSame(
            firstUseCase,
            secondScope.ServiceProvider.GetRequiredService<GetDashboardUseCase>());
    }
}
