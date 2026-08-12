using Enma.Application.Organizations.CurrentUser;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class CurrentUserOrganizationDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_CurrentUserOrganizationQueryGraph_UsesScopedLifetime()
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
        await using AsyncServiceScope firstScope =
            serviceProvider.CreateAsyncScope();
        ICurrentUserOrganizationQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<ICurrentUserOrganizationQueries>();
        GetCurrentUserOrganizationsUseCase firstUseCase =
            firstScope.ServiceProvider
                .GetRequiredService<GetCurrentUserOrganizationsUseCase>();
        EnmaDbContext firstDbContext = firstScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.IsType<CurrentUserOrganizationQueries>(firstQueries);
        Assert.NotNull(firstUseCase);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider
                .GetRequiredService<ICurrentUserOrganizationQueries>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider
                .GetRequiredService<GetCurrentUserOrganizationsUseCase>());

        await using AsyncServiceScope secondScope =
            serviceProvider.CreateAsyncScope();
        ICurrentUserOrganizationQueries secondQueries = secondScope.ServiceProvider
            .GetRequiredService<ICurrentUserOrganizationQueries>();
        GetCurrentUserOrganizationsUseCase secondUseCase =
            secondScope.ServiceProvider
                .GetRequiredService<GetCurrentUserOrganizationsUseCase>();
        EnmaDbContext secondDbContext = secondScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.NotSame(firstQueries, secondQueries);
        Assert.NotSame(firstUseCase, secondUseCase);
        Assert.NotSame(firstDbContext, secondDbContext);
    }
}
