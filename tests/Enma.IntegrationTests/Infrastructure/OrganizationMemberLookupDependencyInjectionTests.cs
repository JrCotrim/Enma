using Enma.Application.Organizations.Members.Lookup;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberLookupDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_OrganizationMemberLookupGraph_UsesScopedLifetime()
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
        IOrganizationMemberLookupQueries firstQueries = firstScope.ServiceProvider
            .GetRequiredService<IOrganizationMemberLookupQueries>();
        SearchActiveOrganizationMembersUseCase firstUseCase = firstScope
            .ServiceProvider
            .GetRequiredService<SearchActiveOrganizationMembersUseCase>();
        EnmaDbContext firstDbContext = firstScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.IsType<OrganizationMemberLookupQueries>(firstQueries);
        Assert.Same(
            firstQueries,
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMemberLookupQueries>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider
                .GetRequiredService<SearchActiveOrganizationMembersUseCase>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();
        IOrganizationMemberLookupQueries secondQueries = secondScope.ServiceProvider
            .GetRequiredService<IOrganizationMemberLookupQueries>();
        SearchActiveOrganizationMembersUseCase secondUseCase = secondScope
            .ServiceProvider
            .GetRequiredService<SearchActiveOrganizationMembersUseCase>();
        EnmaDbContext secondDbContext = secondScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.NotSame(firstQueries, secondQueries);
        Assert.NotSame(firstUseCase, secondUseCase);
        Assert.NotSame(firstDbContext, secondDbContext);
    }
}
