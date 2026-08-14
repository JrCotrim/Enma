using Enma.Application.Tasks;
using Enma.Application.Tasks.Create;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskCreationDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_LegalTaskCreationGraph_UsesScopedLifetimes()
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
        ILegalTaskCreationPersistence firstPersistence = firstScope.ServiceProvider
            .GetRequiredService<ILegalTaskCreationPersistence>();
        CreateLegalTaskUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<CreateLegalTaskUseCase>();

        Assert.IsType<LegalTaskCreationPersistence>(firstPersistence);
        Assert.Same(
            firstPersistence,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalTaskCreationPersistence>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider.GetRequiredService<CreateLegalTaskUseCase>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();
        ILegalTaskCreationPersistence secondPersistence = secondScope.ServiceProvider
            .GetRequiredService<ILegalTaskCreationPersistence>();
        CreateLegalTaskUseCase secondUseCase = secondScope.ServiceProvider
            .GetRequiredService<CreateLegalTaskUseCase>();

        Assert.NotSame(firstPersistence, secondPersistence);
        Assert.NotSame(firstUseCase, secondUseCase);
    }
}
