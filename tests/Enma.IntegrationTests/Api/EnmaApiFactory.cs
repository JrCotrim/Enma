using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Api;

public sealed class EnmaApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgreSqlFixture fixture;
    private readonly Action<IServiceCollection>? configureTestServices;

    public EnmaApiFactory(
        PostgreSqlFixture fixture,
        Action<IServiceCollection>? configureTestServices = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
        this.configureTestServices = configureTestServices;
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
                ["ConnectionStrings:Database"] = fixture.ConnectionString
            });
        });

        if (configureTestServices is not null)
        {
            builder.ConfigureTestServices(configureTestServices);
        }
    }
}
