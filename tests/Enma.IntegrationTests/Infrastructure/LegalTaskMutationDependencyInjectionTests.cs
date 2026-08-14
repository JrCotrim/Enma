using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskMutationDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_LegalTaskMutationGraph_UsesScopedLifetimes()
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
        ILegalTaskMutationPersistence persistence = firstScope.ServiceProvider
            .GetRequiredService<ILegalTaskMutationPersistence>();

        Assert.IsType<LegalTaskMutationPersistence>(persistence);
        Assert.Same(
            persistence,
            firstScope.ServiceProvider
                .GetRequiredService<ILegalTaskMutationPersistence>());
        Assert.Same(
            firstScope.ServiceProvider
                .GetRequiredService<LegalTaskMutationAuthorization>(),
            firstScope.ServiceProvider
                .GetRequiredService<LegalTaskMutationAuthorization>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<UpdateLegalTaskUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<ChangeLegalTaskAssigneeUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<CompleteLegalTaskUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<ReopenLegalTaskUseCase>());

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        Assert.NotSame(
            persistence,
            secondScope.ServiceProvider
                .GetRequiredService<ILegalTaskMutationPersistence>());
    }
}
