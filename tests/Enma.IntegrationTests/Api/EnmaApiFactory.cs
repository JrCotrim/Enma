using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Enma.IntegrationTests.Api;

public sealed class EnmaApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgreSqlFixture fixture;
    private readonly Action<IServiceCollection>? configureTestServices;
    private readonly bool includeBackgroundWorkers;

    public EnmaApiFactory(
        PostgreSqlFixture fixture,
        Action<IServiceCollection>? configureTestServices = null,
        bool includeBackgroundWorkers = false)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
        this.configureTestServices = configureTestServices;
        this.includeBackgroundWorkers = includeBackgroundWorkers;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:Database",
            fixture.ConnectionString);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = fixture.ConnectionString,
                [TestHostIsolation.DevelopmentSmtpPortKey] =
                    TestHostIsolation.UnusedSmtpPort.ToString()
            });
        });

        if (configureTestServices is not null)
        {
            builder.ConfigureTestServices(configureTestServices);
        }

        if (!includeBackgroundWorkers)
        {
            builder.ConfigureTestServices(TestHostIsolation.RemoveBackgroundWorkers);
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        return OrderedHostStart.Start(builder);
    }
}
