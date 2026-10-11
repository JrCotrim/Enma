using System.Net;
using System.Net.Http.Json;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleAuthenticationEndpointTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string FetchSiteHeader = "Sec-Fetch-Site";
    private const string StartPath = "/api/auth/google/start";
    private const string SyntheticInvitationToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

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
        client.DefaultRequestHeaders.Add(FetchSiteHeader, "same-origin");

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
        client.DefaultRequestHeaders.Add(FetchSiteHeader, "same-origin");
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

    // The test client calls the API at http://localhost. The Vite dev and
    // Pilot proxies rewrite Host to the API but keep the browser's Origin
    // (the Vite origin) and Sec-Fetch-Site, so Fetch Metadata must carry them.
    [Theory]
    [InlineData("same-origin", null)]
    [InlineData(null, "http://localhost")]
    [InlineData("same-origin", "http://localhost:5173")]
    public async Task Start_SameOriginRequest_RedirectsToGoogle(
        string? fetchSite,
        string? origin)
    {
        await using WebApplicationFactory<Program> factory = CreateEnabledFactory();
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.SendAsync(
            CreateStartRequest(fetchSite, origin));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("provider.example.test", location.Host);
        Assert.Contains("state=", location.Query);
    }

    [Theory]
    [InlineData("cross-site", null)]
    [InlineData("same-site", null)]
    [InlineData("none", null)]
    [InlineData("cross-site", "http://localhost")]
    [InlineData(null, "https://attacker.example.test")]
    [InlineData(null, "http://localhost:5173")]
    [InlineData(null, null)]
    [InlineData(null, "null")]
    public async Task Start_NotSameOriginRequest_ReturnsNeutralForbidden(
        string? fetchSite,
        string? origin)
    {
        var logs = new CapturingLoggerProvider();
        await using WebApplicationFactory<Program> factory =
            CreateEnabledFactory(logs: logs);
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.SendAsync(
            CreateStartRequest(fetchSite, origin));

        await AssertNeutralForbiddenAsync(response);
        Assert.Contains(
            logs.Entries,
            entry => entry.Message.StartsWith(
                "Google sign-in start rejected",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_CrossSiteRequestWithInvitation_ReturnsNeutralForbiddenWithoutLoggingIt()
    {
        var logs = new CapturingLoggerProvider();
        await using WebApplicationFactory<Program> factory =
            CreateEnabledFactory(logs: logs);
        using HttpClient client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.SendAsync(
            CreateStartRequest(
                "cross-site",
                "https://attacker.example.test",
                SyntheticInvitationToken));

        await AssertNeutralForbiddenAsync(response);
        CapturedLogEntry rejection = Assert.Single(
            logs.Entries,
            entry => entry.Message.StartsWith(
                "Google sign-in start rejected",
                StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, rejection.Level);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Contains(SyntheticInvitationToken) ||
                entry.Contains("attacker.example.test"));
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

    private static HttpRequestMessage CreateStartRequest(
        string? fetchSite,
        string? origin,
        string? invitationToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StartPath)
        {
            Content = new FormUrlEncodedContent(invitationToken is null
                ? []
                : new Dictionary<string, string>
                {
                    ["invitationToken"] = invitationToken
                })
        };
        if (fetchSite is not null)
        {
            request.Headers.TryAddWithoutValidation(FetchSiteHeader, fetchSite);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    private static async Task AssertNeutralForbiddenAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private WebApplicationFactory<Program> CreateEnabledFactory(
        string? frontendOrigin = null,
        CapturingLoggerProvider? logs = null)
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
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }

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
