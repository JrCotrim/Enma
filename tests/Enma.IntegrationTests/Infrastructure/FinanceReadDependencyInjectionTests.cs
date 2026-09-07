using Enma.Application.Finance;
using Enma.Application.Finance.GetById;
using Enma.Application.Finance.List;
using Enma.Application.Finance.Overview;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class FinanceReadDependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_FinanceReadGraph_IsScopedAndComplete()
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
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<FinanceReadQueries>(
            scope.ServiceProvider.GetRequiredService<IFinanceReadQueries>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ListPaymentPlansUseCase>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetPaymentPlanUseCase>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetFinanceOverviewUseCase>());
    }
}
