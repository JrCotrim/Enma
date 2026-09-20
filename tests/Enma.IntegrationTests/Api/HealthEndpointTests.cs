using System.Net;
using Enma.Api.Notifications;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Enma.IntegrationTests.Api;

[Collection(PostgreSqlCollection.Name)]
public sealed class HealthEndpointTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Liveness_WhenDatabaseIsUnavailable_RemainsHealthy()
    {
        string unavailableConnectionString = CreateConnectionString(
            $"missing_{Guid.NewGuid():N}");
        using ConfiguredApiFactory factory = new(unavailableConnectionString);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(
            "/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Readiness_WhenDatabaseAndSchemaAreCurrent_ReturnsMinimalHealthyResponse()
    {
        using ConfiguredApiFactory factory = new(fixture.ConnectionString);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(
            "/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Readiness_WhenSchemaHasPendingMigrations_ReturnsMinimalServiceUnavailable()
    {
        string databaseName = $"enma_readiness_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(databaseName);

        try
        {
            using ConfiguredApiFactory factory = new(
                CreateConnectionString(databaseName));
            using HttpClient client = CreateClient(factory);

            using HttpResponseMessage response = await client.GetAsync(
                "/health/ready");
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                response.StatusCode);
            Assert.Equal("Unhealthy", body);
            Assert.DoesNotContain(databaseName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "migration",
                body,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Readiness_WhenDatabaseIsUnavailable_ReturnsMinimalServiceUnavailable()
    {
        string databaseName = $"missing_{Guid.NewGuid():N}";
        using ConfiguredApiFactory factory = new(
            CreateConnectionString(databaseName));
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(
            "/health/ready");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain(databaseName, body, StringComparison.Ordinal);
    }

    private string CreateConnectionString(string databaseName)
    {
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName,
            Timeout = 1,
            CommandTimeout = 1
        }.ConnectionString;
    }

    private async Task CreateDatabaseAsync(string databaseName)
    {
        await ExecuteDatabaseAdministrationAsync(
            $"CREATE DATABASE {databaseName};");
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteDatabaseAdministrationAsync(
            $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
    }

    private async Task ExecuteDatabaseAdministrationAsync(string commandText)
    {
        string administrationConnectionString =
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Database = "postgres"
            }.ConnectionString;
        await using var connection = new NpgsqlConnection(
            administrationConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    private sealed class ConfiguredApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:Database",
                connectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Database"] = connectionString
                    }));
            builder.ConfigureTestServices(services =>
            {
                ServiceDescriptor[] workers = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType ==
                            typeof(NotificationGenerationWorker))
                    .ToArray();
                foreach (ServiceDescriptor worker in workers)
                {
                    services.Remove(worker);
                }
            });
        }
    }
}
