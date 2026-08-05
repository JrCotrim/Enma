using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Enma.IntegrationTests.Api;

public sealed class EnmaApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgreSqlFixture fixture;

    public EnmaApiFactory(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
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
    }
}
