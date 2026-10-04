using System.Net;
using Enma.IntegrationTests.Infrastructure.Persistence;

namespace Enma.IntegrationTests.Api;

[Collection(PostgreSqlCollection.Name)]
public sealed class RemovedTemplateEndpointTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData("/weatherforecast")]
    [InlineData("/WeatherForecast")]
    public async Task TemplateWeatherForecastRoute_IsNotExposed(string path)
    {
        await using var factory = new EnmaApiFactory(fixture);
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
