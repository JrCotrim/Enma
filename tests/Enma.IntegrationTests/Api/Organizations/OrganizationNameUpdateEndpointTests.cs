using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
using Enma.Application.Organizations.UpdateName;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationNameUpdateEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string CsrfPath = "/api/auth/csrf";
    private const string PasswordHash = "synthetic-organization-name-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        26,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationNameUpdateEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public void RequestContract_ContainsOnlyName()
    {
        Assert.Equal(
            [nameof(UpdateOrganizationNameRequest.Name)],
            typeof(UpdateOrganizationNameRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task UpdateName_Anonymous_ReturnsEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            GetUpdatePath(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new { name = "New Legal" })
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateName_InvalidSession_ReturnsEmptyNoStoreUnauthorized()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            GetUpdatePath(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new { name = "New Legal" })
        };
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}=synthetic-invalid-session");

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateName_Owner_NormalizesAndReturnsEmptyNoStoreNoContent()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        string originalSlug = graph.Organization.Slug;
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = "  Renamed Legal  " });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        OrganizationState state = await FindOrganizationAsync(
            graph.Organization.Id);
        Assert.Equal("Renamed Legal", state.Name);
        Assert.Equal(originalSlug, state.Slug);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task UpdateName_NonOwner_ReturnsEmptyNoStoreForbiddenWithoutMutation(
        OrganizationRole role)
    {
        TestGraph graph = await SeedGraphAsync(role);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = "Denied Legal" });

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateName_EmptyOrWhitespace_ReturnsSafeNoStoreBadRequest(
        string name)
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name });

        await AssertSafeProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_OverMaximumLength_ReturnsSafeNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = new string('x', 151) });

        await AssertSafeProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_MissingName_ReturnsNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_MalformedJson_ReturnsNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);
        using HttpRequestMessage request = CreateUpdateRequest(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf);
        request.Content = new StringContent(
            "{\"name\":",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_MissingOrInvalidAntiforgery_ReturnsEmptyNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage missing = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf: null,
            new { name = "Denied Legal" });
        using HttpResponseMessage invalid = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = "Denied Legal" },
            requestTokenOverride: "invalid-antiforgery-token");

        await AssertEmptyResponseAsync(missing, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(invalid, HttpStatusCode.BadRequest);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_InaccessibleAndNonexistentOrganizations_ReturnSameSafeForbidden()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        Organization foreignOrganization = CreateOrganization("Foreign");
        await SeedAsync(foreignOrganization);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage foreign = await SendUpdateAsync(
            foreignOrganization.Id,
            graph.ActorHandle,
            csrf,
            new { name = "Denied Legal" });
        using HttpResponseMessage missing = await SendUpdateAsync(
            Guid.NewGuid(),
            graph.ActorHandle,
            csrf,
            new { name = "Denied Legal" });

        await AssertEmptyResponseAsync(foreign, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(missing, HttpStatusCode.Forbidden);
        Assert.Equal(
            foreignOrganization.Name,
            (await FindOrganizationAsync(foreignOrganization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_SameNormalizedName_IsAuthorizedNoContent()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = $"  {graph.Organization.Name}  " });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task UpdateName_Success_IsImmediatelyVisibleInOrganizationDiscovery()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage update = await SendUpdateAsync(
            graph.Organization.Id,
            graph.ActorHandle,
            csrf,
            new { name = "Discovered Legal" });
        using HttpResponseMessage discovery = await SendGetAsync(
            "/api/me/organizations",
            graph.ActorHandle);

        await AssertEmptyResponseAsync(update, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.True(discovery.Headers.CacheControl?.NoStore);
        GetCurrentUserOrganizationsResponse payload = Assert.IsType<
            GetCurrentUserOrganizationsResponse>(
                await discovery.Content
                    .ReadFromJsonAsync<GetCurrentUserOrganizationsResponse>());
        CurrentUserOrganizationResponse item = Assert.Single(payload.Items);
        Assert.Equal(graph.Organization.Id, item.Id);
        Assert.Equal("Discovered Legal", item.Name);
        Assert.Equal("Owner", item.Role);
        Assert.Equal(graph.ActorMembership.Id, item.MembershipId);
    }

    [Fact]
    public async Task OrganizationNameMutationServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();

        AssertScoped<UpdateOrganizationNameUseCase>(firstScope, secondScope);
        AssertScoped<IOrganizationNameMutationPersistence>(
            firstScope,
            secondScope);
    }

    private async Task<TestGraph> SeedGraphAsync(OrganizationRole role)
    {
        Organization organization = CreateOrganization("Current");
        User actor = CreateUser("Actor");
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            role,
            Now.AddHours(-1));
        AuthenticatedSession authenticatedSession = CreateSession(actor);

        await SeedAsync(
            organization,
            actor,
            membership,
            authenticatedSession.Credential,
            authenticatedSession.Session);

        return new TestGraph(
            organization,
            membership,
            authenticatedSession.RawHandle);
    }

    private AuthenticatedSession CreateSession(User user)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));
        return new AuthenticatedSession(rawHandle, credential, session);
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse payload = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
        return new CsrfPair(payload.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendUpdateAsync(
        Guid organizationId,
        string rawHandle,
        CsrfPair? csrf,
        object body,
        string? requestTokenOverride = null)
    {
        using HttpRequestMessage request = CreateUpdateRequest(
            organizationId,
            rawHandle,
            csrf,
            requestTokenOverride);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateUpdateRequest(
        Guid organizationId,
        string rawHandle,
        CsrfPair? csrf,
        string? requestTokenOverride = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            GetUpdatePath(organizationId));
        var cookies = new List<string> { $"{SessionCookieName}={rawHandle}" };

        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        string? requestToken = requestTokenOverride ?? csrf?.RequestToken;

        if (requestToken is not null)
        {
            request.Headers.Add(CsrfHeaderName, requestToken);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<OrganizationState> FindOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => new OrganizationState(
                organization.Name,
                organization.Slug))
            .SingleAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant()}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant()}+{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
    }

    private static string GetUpdatePath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}";
    }

    private static IReadOnlyList<SetCookieHeaderValue> ParseSetCookies(
        HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(
            HeaderNames.SetCookie,
            out IEnumerable<string>? values)
                ? SetCookieHeaderValue.ParseList(values.ToList()).ToArray()
                : [];
    }

    private static void AssertScoped<TService>(
        AsyncServiceScope firstScope,
        AsyncServiceScope secondScope)
        where TService : class
    {
        TService first = firstScope.ServiceProvider.GetRequiredService<TService>();
        Assert.Same(
            first,
            firstScope.ServiceProvider.GetRequiredService<TService>());
        Assert.NotSame(
            first,
            secondScope.ServiceProvider.GetRequiredService<TService>());
    }

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertSafeProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", content);
        Assert.DoesNotContain("stackTrace", content);
        Assert.DoesNotContain("userId", content);
        Assert.DoesNotContain("organizationId", content);
        Assert.DoesNotContain("membershipId", content);
        Assert.DoesNotContain("Owner", content);
    }

    private sealed record TestGraph(
        Organization Organization,
        OrganizationMembership ActorMembership,
        string ActorHandle);

    private sealed record AuthenticatedSession(
        string RawHandle,
        UserCredential Credential,
        AuthenticationSession Session);

    private sealed record OrganizationState(string Name, string Slug);

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
