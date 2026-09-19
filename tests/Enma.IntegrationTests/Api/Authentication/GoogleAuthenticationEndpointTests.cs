using System.Net;
using System.Net.Http.Json;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleAuthenticationEndpointTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Providers_GoogleDisabled_ReturnsFalseAndStartIsUnavailable()
    {
        await using var factory = new EnmaApiFactory(fixture);
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage providers = await client.GetAsync(
            "/api/auth/providers");
        HttpResponseMessage start = await client.PostAsync(
            "/api/auth/google/start",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.OK, providers.StatusCode);
        var payload = await providers.Content.ReadFromJsonAsync<ProvidersResponse>();
        Assert.NotNull(payload);
        Assert.False(payload.Google);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Fact]
    public async Task Start_GoogleEnabled_UsesProtectedOidcStateNonceAndPkce()
    {
        await using WebApplicationFactory<Program> factory = CreateEnabledFactory();
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });
        const string invitationToken =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        HttpResponseMessage malformed = await client.PostAsync(
            "/api/auth/google/start",
            new StringContent(string.Empty));
        HttpResponseMessage response = await client.PostAsync(
            "/api/auth/google/start",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["invitationToken"] = invitationToken
            }));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("provider.example.test", location.Host);
        Assert.Contains("response_type=code", location.Query);
        Assert.Contains("state=", location.Query);
        Assert.Contains("nonce=", location.Query);
        Assert.Contains("code_challenge=", location.Query);
        Assert.DoesNotContain(invitationToken, location.OriginalString);
    }

    [Fact]
    public async Task Callback_InvalidState_FailsToFixedInternalRoute()
    {
        await using WebApplicationFactory<Program> factory = CreateEnabledFactory(
            "https://frontend.example.test");
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync(
            "/signin-google?code=synthetic&state=invalid");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "https://frontend.example.test/login?google=failed",
            response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("Authentication:Google:Enabled", "true")]
    [InlineData(
        "Authentication:Google:FrontendOrigin",
        "https://frontend.example.test/untrusted-path")]
    [InlineData(
        "Authentication:Google:FrontendOrigin",
        "http://frontend.example.test")]
    public void Startup_InvalidGoogleConfiguration_FailsClosed(
        string key,
        string value)
    {
        var baseFactory = new EnmaApiFactory(fixture);
        using WebApplicationFactory<Program> factory = baseFactory
            .WithWebHostBuilder(builder => builder.UseSetting(key, value));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private WebApplicationFactory<Program> CreateEnabledFactory(
        string? frontendOrigin = null)
    {
        var baseFactory = new EnmaApiFactory(fixture);
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:Google:Enabled", "true");
            builder.UseSetting(
                "Authentication:Google:ClientId",
                "synthetic-client");
            builder.UseSetting(
                "Authentication:Google:ClientSecret",
                "synthetic-secret");
            if (frontendOrigin is not null)
            {
                builder.UseSetting(
                    "Authentication:Google:FrontendOrigin",
                    frontendOrigin);
            }
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>("Google", options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = "https://accounts.google.com",
                        AuthorizationEndpoint =
                            "https://provider.example.test/authorize",
                        TokenEndpoint = "https://provider.example.test/token"
                    };
                    options.Configuration = configuration;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(
                            configuration);
                });
            });
        });
    }

    private sealed record ProvidersResponse(bool Google);
}
