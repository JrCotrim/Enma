using System.Net;
using System.Net.Http.Json;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
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
public sealed class OrganizationMemberLifecycleEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string CsrfPath = "/api/auth/csrf";
    private const string PasswordHash =
        "synthetic-member-lifecycle-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        26,
        17,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationMemberLifecycleEndpointTests(PostgreSqlFixture fixture)
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

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_Anonymous_ReturnsEmptyNoStoreUnauthorizedBeforeCsrf(
        LifecycleEndpoint endpoint)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetPath(Guid.NewGuid(), Guid.NewGuid(), endpoint));

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate, OrganizationRole.Member)]
    [InlineData(LifecycleEndpoint.Deactivate, OrganizationRole.Administrator)]
    [InlineData(LifecycleEndpoint.Reactivate, OrganizationRole.Member)]
    [InlineData(LifecycleEndpoint.Reactivate, OrganizationRole.Administrator)]
    public async Task Lifecycle_Owner_ManagesSupportedTarget(
        LifecycleEndpoint endpoint,
        OrganizationRole targetRole)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            targetRole,
            targetMembershipActive: endpoint == LifecycleEndpoint.Deactivate);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(
            endpoint == LifecycleEndpoint.Reactivate,
            await FindMembershipActivityAsync(graph.TargetMembership.Id));
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_Administrator_ManagesMember(
        LifecycleEndpoint endpoint)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Administrator,
            OrganizationRole.Member,
            targetMembershipActive: endpoint == LifecycleEndpoint.Deactivate);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate, OrganizationRole.Member,
        OrganizationRole.Member)]
    [InlineData(LifecycleEndpoint.Reactivate, OrganizationRole.Member,
        OrganizationRole.Member)]
    [InlineData(LifecycleEndpoint.Deactivate, OrganizationRole.Administrator,
        OrganizationRole.Administrator)]
    [InlineData(LifecycleEndpoint.Reactivate, OrganizationRole.Administrator,
        OrganizationRole.Administrator)]
    public async Task Lifecycle_ForbiddenActorTargetMatrix_ReturnsEmptyNoStoreForbidden(
        LifecycleEndpoint endpoint,
        OrganizationRole actorRole,
        OrganizationRole targetRole)
    {
        bool initiallyActive = endpoint == LifecycleEndpoint.Deactivate;
        TestGraph graph = await SeedGraphAsync(
            actorRole,
            targetRole,
            initiallyActive);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(
            initiallyActive,
            await FindMembershipActivityAsync(graph.TargetMembership.Id));
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_OwnerTarget_ReturnsForbidden(
        LifecycleEndpoint endpoint)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Owner,
            targetMembershipActive: endpoint == LifecycleEndpoint.Deactivate);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_SelfTarget_ReturnsForbidden(
        LifecycleEndpoint endpoint)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint,
            graph.ActorMembership.Id);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_CrossTenantTarget_MatchesMissingNotFound(
        LifecycleEndpoint endpoint)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            targetMembershipActive: endpoint == LifecycleEndpoint.Deactivate);
        Organization foreignOrganization = CreateOrganization("Foreign");
        User foreignUser = CreateUser("Foreign Target");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            Now.AddDays(-1));
        await SeedAsync(foreignOrganization, foreignUser, foreignMembership);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage foreign = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint,
            foreignMembership.Id);
        using HttpResponseMessage missing = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint,
            Guid.Parse("48fb494a-f061-4630-a6e2-35589d2ab116"));

        await AssertEmptyResponseAsync(foreign, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missing, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate, false)]
    [InlineData(LifecycleEndpoint.Reactivate, true)]
    public async Task Lifecycle_AlreadyFinalState_ReturnsEmptyNoContent(
        LifecycleEndpoint endpoint,
        bool initiallyActive)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            initiallyActive);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deactivate_WithOpenAssignment_ReturnsSafeNoStoreConflict()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        var task = new LegalTask(
            graph.Organization.Id,
            "Blocking task",
            null,
            null,
            null,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            Now.AddHours(-1));
        await SeedAsync(task);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            LifecycleEndpoint.Deactivate);

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        string problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("active assigned work", problem);
    }

    [Fact]
    public async Task Reactivate_WithInactiveUser_ReturnsSafeNoStoreConflict()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            targetMembershipActive: false,
            targetUserActive: false);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            LifecycleEndpoint.Reactivate);

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        string problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("account is inactive", problem);
    }

    [Theory]
    [InlineData(LifecycleEndpoint.Deactivate)]
    [InlineData(LifecycleEndpoint.Reactivate)]
    public async Task Lifecycle_MissingOrInvalidAntiforgery_ReturnsEmptyNoStoreBadRequest(
        LifecycleEndpoint endpoint)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            targetMembershipActive: endpoint == LifecycleEndpoint.Deactivate);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage missing = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf: null,
            endpoint);
        using HttpResponseMessage invalid = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            endpoint,
            requestTokenOverride: "invalid-antiforgery-token");

        await AssertEmptyResponseAsync(missing, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(invalid, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SameSession_LosesAndRegainsLiveOrganizationAccess()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair ownerCsrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage before = await SendMemberListAsync(
            graph.Organization.Id,
            graph.TargetHandle);
        using HttpResponseMessage deactivation = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            ownerCsrf,
            LifecycleEndpoint.Deactivate);
        using HttpResponseMessage whileInactive = await SendMemberListAsync(
            graph.Organization.Id,
            graph.TargetHandle);
        using HttpResponseMessage reactivation = await SendLifecycleAsync(
            graph,
            graph.ActorHandle,
            ownerCsrf,
            LifecycleEndpoint.Reactivate);
        using HttpResponseMessage after = await SendMemberListAsync(
            graph.Organization.Id,
            graph.TargetHandle);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.True(before.Headers.CacheControl?.NoStore);
        await AssertEmptyResponseAsync(deactivation, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(whileInactive, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(reactivation, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.True(after.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task LifecycleServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope =
            factory.Services.CreateAsyncScope();

        AssertScoped<OrganizationMemberLifecycleUseCase>(firstScope, secondScope);
        AssertScoped<IOrganizationMemberLifecycleMutationPersistence>(
            firstScope,
            secondScope);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole,
        OrganizationRole targetRole,
        bool targetMembershipActive = true,
        bool targetUserActive = true)
    {
        Organization organization = CreateOrganization("Current");
        User actor = CreateUser("Actor");
        User target = CreateUser("Target");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            actorRole,
            Now.AddDays(-1));
        var targetMembership = new OrganizationMembership(
            organization.Id,
            target.Id,
            targetRole,
            Now.AddDays(-1));

        if (!targetMembershipActive)
        {
            targetMembership.Deactivate();
        }

        if (!targetUserActive)
        {
            target.Deactivate();
        }

        AuthenticatedSession actorSession = CreateSession(actor);
        AuthenticatedSession targetSession = CreateSession(target);
        await SeedAsync(
            organization,
            actor,
            target,
            actorMembership,
            targetMembership,
            actorSession.Credential,
            actorSession.Session,
            targetSession.Credential,
            targetSession.Session);

        return new TestGraph(
            organization,
            actorMembership,
            targetMembership,
            actorSession.RawHandle,
            targetSession.RawHandle);
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
        CsrfResponse result = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendLifecycleAsync(
        TestGraph graph,
        string rawHandle,
        CsrfPair? csrf,
        LifecycleEndpoint endpoint,
        Guid? membershipId = null,
        string? requestTokenOverride = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetPath(
                graph.Organization.Id,
                membershipId ?? graph.TargetMembership.Id,
                endpoint));
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

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMemberListAsync(
        Guid organizationId,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/organizations/{organizationId:D}/members");
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<bool> FindMembershipActivityAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.IsActive)
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
            Now.AddDays(-2));
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            Now.AddDays(-2));
    }

    private static string GetPath(
        Guid organizationId,
        Guid membershipId,
        LifecycleEndpoint endpoint)
    {
        string operation = endpoint == LifecycleEndpoint.Deactivate
            ? "deactivate"
            : "reactivate";
        return $"/api/organizations/{organizationId:D}/members/{membershipId:D}/{operation}";
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

    private static async Task AssertProblemResponseAsync(
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
        Assert.DoesNotContain("organizationId", content);
        Assert.DoesNotContain("membershipId", content);
    }

    public enum LifecycleEndpoint
    {
        Deactivate,
        Reactivate
    }

    private sealed record TestGraph(
        Organization Organization,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership,
        string ActorHandle,
        string TargetHandle);

    private sealed record AuthenticatedSession(
        string RawHandle,
        UserCredential Credential,
        AuthenticationSession Session);

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
