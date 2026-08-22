using Enma.Application.Agenda;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class AgendaDependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_AgendaReadGraph_IsScopedAndComplete()
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

        IAgendaReadQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<IAgendaReadQueries>();
        GetAgendaUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<GetAgendaUseCase>();

        Assert.IsType<AgendaReadQueries>(firstQueries);
        Assert.NotNull(firstUseCase);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider.GetRequiredService<IAgendaReadQueries>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider.GetRequiredService<GetAgendaUseCase>());

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        Assert.NotSame(
            firstQueries,
            secondScope.ServiceProvider.GetRequiredService<IAgendaReadQueries>());
        Assert.NotSame(
            firstUseCase,
            secondScope.ServiceProvider.GetRequiredService<GetAgendaUseCase>());
    }
}
