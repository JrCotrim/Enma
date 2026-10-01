using Enma.Application.Dashboard;
using Enma.Application.Finance.Overview;
using Enma.Application.Notifications;
using Enma.Application.Time;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Time;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Time;

[Collection(PostgreSqlCollection.Name)]
public sealed class OperationalTimeZoneDependencyInjectionTests(
    PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_WithoutSection_UsesSaoPauloSingletonCalendar()
    {
        await using ServiceProvider serviceProvider = BuildServiceProvider(
            new ConfigurationBuilder().Build());
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        OperationalCalendar calendar =
            serviceProvider.GetRequiredService<OperationalCalendar>();

        Assert.Equal("America/Sao_Paulo", calendar.TimeZone.Id);
        Assert.Same(
            calendar,
            scope.ServiceProvider.GetRequiredService<OperationalCalendar>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<GenerateNotificationsUseCase>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<GetDashboardUseCase>());
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<GetFinanceOverviewUseCase>());
    }

    [Fact]
    public async Task AddInfrastructure_ConfiguredTimeZone_DrivesCalendar()
    {
        await using ServiceProvider serviceProvider = BuildServiceProvider(
            CreateConfiguration("Asia/Tokyo"));

        OperationalCalendar calendar =
            serviceProvider.GetRequiredService<OperationalCalendar>();

        Assert.Equal("Asia/Tokyo", calendar.TimeZone.Id);
    }

    [Theory]
    [InlineData("Invalid/Zone")]
    [InlineData("")]
    public async Task AddInfrastructure_InvalidTimeZone_FailsOnResolution(
        string timeZoneId)
    {
        await using ServiceProvider serviceProvider = BuildServiceProvider(
            CreateConfiguration(timeZoneId));

        OptionsValidationException exception =
            Assert.Throws<OptionsValidationException>(
                () => serviceProvider.GetRequiredService<OperationalCalendar>());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                "OperationalTimeZone:TimeZoneId",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddInfrastructure_InvalidTimeZone_FailsHostStartup()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddInfrastructure(
            fixture.ConnectionString,
            CreateConfiguration("Invalid/Zone"),
            isDevelopment: true);
        using IHost host = builder.Build();

        OptionsValidationException exception =
            await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync());

        Assert.Equal(typeof(OperationalTimeZoneOptions), exception.OptionsType);
    }

    [Fact]
    public async Task Api_InvalidTimeZone_FailsStartup()
    {
        await fixture.ResetDatabaseAsync();
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();
        }

        await using var factory = new EnmaApiFactory(fixture);
        await using var invalidFactory = factory.WithWebHostBuilder(
            builder => builder.UseSetting(
                $"{OperationalTimeZoneOptions.SectionName}:TimeZoneId",
                "Invalid/Zone"));

        OptionsValidationException exception =
            Assert.Throws<OptionsValidationException>(
                () => invalidFactory.CreateClient());

        Assert.Equal(typeof(OperationalTimeZoneOptions), exception.OptionsType);
    }

    private ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddInfrastructure(
            fixture.ConnectionString,
            configuration,
            isDevelopment: true);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static IConfiguration CreateConfiguration(string timeZoneId)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OperationalTimeZoneOptions.SectionName}:TimeZoneId"] =
                    timeZoneId
            })
            .Build();
    }
}
