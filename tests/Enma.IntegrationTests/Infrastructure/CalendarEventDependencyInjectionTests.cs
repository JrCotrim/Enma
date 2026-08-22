using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Assignment;
using Enma.Application.CalendarEvents.Create;
using Enma.Application.CalendarEvents.Delete;
using Enma.Application.CalendarEvents.GetById;
using Enma.Application.CalendarEvents.Update;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_CalendarEventApplicationGraph_IsScopedAndComplete()
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

        Assert.IsType<CalendarEventCreationPersistence>(firstScope.ServiceProvider
            .GetRequiredService<ICalendarEventCreationPersistence>());
        Assert.IsType<CalendarEventMutationPersistence>(firstScope.ServiceProvider
            .GetRequiredService<ICalendarEventMutationPersistence>());
        Assert.IsType<CalendarEventReadQueries>(firstScope.ServiceProvider
            .GetRequiredService<ICalendarEventReadQueries>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<CalendarEventAccessAuthorization>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<CalendarEventActionAuthorization>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<CreateCalendarEventUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<GetCalendarEventUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<UpdateCalendarEventUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<ChangeCalendarEventAssigneeUseCase>());
        Assert.NotNull(firstScope.ServiceProvider
            .GetRequiredService<DeleteCalendarEventUseCase>());

        ICalendarEventMutationPersistence firstPersistence =
            firstScope.ServiceProvider
                .GetRequiredService<ICalendarEventMutationPersistence>();
        Assert.Same(
            firstPersistence,
            firstScope.ServiceProvider
                .GetRequiredService<ICalendarEventMutationPersistence>());

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        Assert.NotSame(
            firstPersistence,
            secondScope.ServiceProvider
                .GetRequiredService<ICalendarEventMutationPersistence>());
    }
}
