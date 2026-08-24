using Enma.Api.Notifications;
using Enma.Application.Notifications;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationGenerationDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_RegistersScopedGenerationGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(
            fixture.ConnectionString,
            new ConfigurationBuilder().Build(),
            isDevelopment: true);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        await using AsyncServiceScope firstScope =
            serviceProvider.CreateAsyncScope();
        await using AsyncServiceScope secondScope =
            serviceProvider.CreateAsyncScope();

        INotificationGenerationPersistence firstPersistence = firstScope
            .ServiceProvider
            .GetRequiredService<INotificationGenerationPersistence>();
        INotificationGenerationPersistence secondPersistence = secondScope
            .ServiceProvider
            .GetRequiredService<INotificationGenerationPersistence>();
        GenerateNotificationsUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<GenerateNotificationsUseCase>();
        GenerateNotificationsUseCase secondUseCase = secondScope.ServiceProvider
            .GetRequiredService<GenerateNotificationsUseCase>();

        Assert.IsType<NotificationGenerationPersistence>(firstPersistence);
        Assert.NotSame(firstPersistence, secondPersistence);
        Assert.NotSame(firstUseCase, secondUseCase);
        Assert.Same(
            firstPersistence,
            firstScope.ServiceProvider
                .GetRequiredService<INotificationGenerationPersistence>());
        Assert.Same(
            firstUseCase,
            firstScope.ServiceProvider
                .GetRequiredService<GenerateNotificationsUseCase>());
    }

    [Fact]
    public async Task Program_RegistersSingleNotificationGenerationWorker()
    {
        await fixture.ResetDatabaseAsync();
        await using var factory = new EnmaApiFactory(fixture);

        IHostedService[] hostedServices = factory.Services
            .GetServices<IHostedService>()
            .ToArray();

        Assert.Single(
            hostedServices,
            service => service is NotificationGenerationWorker);
        Assert.IsType<PeriodicNotificationGenerationCycleDelay>(
            factory.Services
                .GetRequiredService<INotificationGenerationCycleDelay>());
    }
}
