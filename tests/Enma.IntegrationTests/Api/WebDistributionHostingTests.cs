using System.Net;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api;

// The Production image serves the Enma.Web build from the API web root (D3).
// These hosts run in Production (and Pilot/Development where stated) against
// a synthetic build, so they do not depend on a local `npm run build`.
[Collection(PostgreSqlCollection.Name)]
public sealed class WebDistributionHostingTests : IAsyncLifetime
{
    private const string PublicOrigin = "https://app.example.test";
    private const string IndexMarker = "enma-web-distribution-test-index";
    private const string AssetPath = "/assets/index-Ab12_cD3.js";
    private const string AssetContent = "export const marker = 1;";
    private const string ImmutableCacheControl =
        "public, max-age=31536000, immutable";
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-web-distribution-password-hash";

    private readonly PostgreSqlFixture fixture;
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"enma-web-distribution-{Guid.NewGuid():N}");

    public WebDistributionHostingTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
    }

    private string DistributionPath => Path.Combine(testRoot, "dist");

    private string DataProtectionKeysPath => Path.Combine(testRoot, "keys");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(DistributionPath, "assets"));
        Directory.CreateDirectory(DataProtectionKeysPath);
        await File.WriteAllTextAsync(
            Path.Combine(DistributionPath, "index.html"),
            $"<!doctype html><html><head><title>{IndexMarker}</title></head>" +
            "<body><div id=\"root\"></div></body></html>");
        await File.WriteAllTextAsync(
            Path.Combine(DistributionPath, "assets", Path.GetFileName(AssetPath)),
            AssetContent);
        await fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Pilot")]
    public async Task Root_ReturnsIndexWithoutCache(string environment)
    {
        using WebDistributionFactory factory = CreateFactory(environment);
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/");

        await AssertIndexAsync(response);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/organizations/0b6f8a59-1c1c-4d8f-9a63-1a2f0f7f3c11/clients")]
    [InlineData("/verify-email?token=synthetic")]
    public async Task SpaDeepLink_ReturnsIndexWithoutCache(string path)
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(path);

        await AssertIndexAsync(response);
    }

    [Fact]
    public async Task IndexFile_RequestedDirectly_IsServedWithoutCache()
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/index.html");

        await AssertIndexAsync(response);
    }

    [Fact]
    public async Task HashedAsset_ReturnsContentTypeAndImmutableCache()
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(AssetPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/javascript",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            ImmutableCacheControl,
            response.Headers.CacheControl?.ToString());
        Assert.Equal(AssetContent, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/api/does-not-exist")]
    [InlineData("GET", "/api/organizations/not-a-route/anything")]
    [InlineData("POST", "/api/does-not-exist")]
    [InlineData("GET", "/health/does-not-exist")]
    [InlineData("GET", "/assets/missing-Zz99_yY8.js")]
    public async Task UnknownApiHealthOrAssetPath_ReturnsEmptyNotFoundWithoutHtml(
        string method,
        string path)
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);
        using HttpRequestMessage request = new(new HttpMethod(method), path);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreUnchanged(string path)
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrossTenantResourceAndOrganization_StayEmptyAndMatchNonexistent()
    {
        using WebDistributionFactory factory = CreateFactory("Production");
        using HttpClient client = CreateClient(factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        User user = new(
            "Web Distribution Member",
            $"web-distribution-{Guid.NewGuid():N}@example.test",
            now.AddHours(-2));
        user.VerifyEmail(now.AddHours(-1));
        Organization organizationA = new(
            "Web Distribution A Legal",
            $"web-distribution-a-{Guid.NewGuid():N}",
            now.AddHours(-2));
        Organization organizationB = new(
            "Web Distribution B Legal",
            $"web-distribution-b-{Guid.NewGuid():N}",
            now.AddHours(-2));
        OrganizationMembership membershipA = new(
            organizationA.Id,
            user.Id,
            OrganizationRole.Member,
            now.AddHours(-1));
        ClientEntity clientB = new(organizationB.Id, "Client B", now.AddMinutes(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            factory,
            now,
            user,
            [organizationA, organizationB],
            membershipA,
            clientB);

        using HttpResponseMessage crossTenantResource = await SendGetAsync(
            client,
            $"/api/organizations/{organizationA.Id:D}/clients/{clientB.Id:D}",
            rawHandle);
        using HttpResponseMessage missingResource = await SendGetAsync(
            client,
            $"/api/organizations/{organizationA.Id:D}/clients/{Guid.NewGuid():D}",
            rawHandle);
        using HttpResponseMessage foreignOrganization = await SendGetAsync(
            client,
            $"/api/organizations/{organizationB.Id:D}/clients",
            rawHandle);
        using HttpResponseMessage missingOrganization = await SendGetAsync(
            client,
            $"/api/organizations/{Guid.NewGuid():D}/clients",
            rawHandle);

        await AssertEmptyNoStoreAsync(crossTenantResource, HttpStatusCode.NotFound);
        await AssertEmptyNoStoreAsync(missingResource, HttpStatusCode.NotFound);
        await AssertEmptyNoStoreAsync(foreignOrganization, HttpStatusCode.Forbidden);
        await AssertEmptyNoStoreAsync(missingOrganization, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Development_DoesNotServeWebRoot()
    {
        using WebDistributionFactory factory = CreateFactory("Development");
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

        using HttpResponseMessage root = await client.GetAsync("/");
        using HttpResponseMessage asset = await client.GetAsync(AssetPath);

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
    }

    private WebDistributionFactory CreateFactory(string environment)
    {
        Dictionary<string, string?> settings = environment switch
        {
            "Production" => ProductionIngressTests.ValidProductionRuntimeSettings(),
            "Pilot" => ProductionIngressTests.ValidPilotRuntimeSettings(),
            _ => []
        };
        settings["ConnectionStrings:Database"] = fixture.AppConnectionString;
        settings["DataProtection:KeysPath"] = DataProtectionKeysPath;
        settings[TestHostIsolation.DevelopmentSmtpPortKey] =
            TestHostIsolation.UnusedSmtpPort.ToString();
        if (environment != "Development")
        {
            settings["AllowedHosts"] = new Uri(PublicOrigin).Host;
        }

        if (environment == "Production")
        {
            settings["Deployment:TrustedProxy:Enabled"] = "true";
            settings["Deployment:TrustedProxy:KnownProxies:0"] = "192.0.2.10";
        }

        return new WebDistributionFactory(environment, settings, DistributionPath);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(PublicOrigin),
            HandleCookies = false
        });
    }

    private static async Task AssertIndexAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.False(response.Headers.CacheControl?.Public);
        Assert.Contains(
            IndexMarker,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static async Task AssertEmptyNoStoreAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now,
        User user,
        IReadOnlyCollection<Organization> organizations,
        OrganizationMembership membership,
        ClientEntity client)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        UserCredential credential = new(user.Id, PasswordHash, now.AddHours(-1));
        AuthenticationSession session = new(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            now.AddMinutes(-30),
            now.AddMinutes(10),
            now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.Add(membership);
        dbContext.Clients.Add(client);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string path,
        string rawHandle)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private sealed class WebDistributionFactory(
        string environment,
        IReadOnlyDictionary<string, string?> settings,
        string distributionPath)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseWebRoot(distributionPath);

            // Program reads some values before the host is built.
            foreach ((string key, string? value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(
                TestHostIsolation.RemoveBackgroundWorkers);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            return OrderedHostStart.Start(builder);
        }
    }
}
