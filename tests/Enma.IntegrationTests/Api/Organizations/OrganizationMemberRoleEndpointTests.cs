using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Members.Role;
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
public sealed class OrganizationMemberRoleEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string CsrfPath = "/api/auth/csrf";
    private const string PasswordHash = "synthetic-member-role-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        25,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationMemberRoleEndpointTests(PostgreSqlFixture fixture)
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
    public void RequestContract_ContainsOnlySupportedConcurrencyFields()
    {
        Assert.Equal(
            [nameof(ChangeOrganizationMemberRoleRequest.ExpectedCurrentRole),
                nameof(ChangeOrganizationMemberRoleRequest.Role)],
            typeof(ChangeOrganizationMemberRoleRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray());
    }

    [Fact]
    public async Task ChangeRole_Anonymous_ReturnsEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            GetRolePath(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(CreateBody("Administrator", "Member"))
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Member, "Administrator", "Member",
        OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Administrator, "Member", "Administrator",
        OrganizationRole.Member)]
    public async Task ChangeRole_Owner_SucceedsBothDirections(
        OrganizationRole currentRole,
        string role,
        string expectedCurrentRole,
        OrganizationRole persistedRole)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            currentRole);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody(role, expectedCurrentRole));

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(
            persistedRole,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ChangeRole_NonOwner_ReturnsEmptyNoStoreForbiddenWithoutMutation(
        OrganizationRole actorRole)
    {
        TestGraph graph = await SeedGraphAsync(
            actorRole,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_CrossTenantTarget_MatchesNonexistentNotFound()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        Organization foreignOrganization = CreateOrganization("Foreign");
        User foreignUser = CreateUser("Foreign Target");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        await SeedAsync(foreignOrganization, foreignUser, foreignMembership);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage foreignResponse = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"),
            foreignMembership.Id);
        using HttpResponseMessage missingResponse = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"),
            Guid.NewGuid());

        await AssertEmptyResponseAsync(foreignResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(foreignMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_OwnerTarget_ReturnsEmptyNoStoreForbidden()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Member", "Administrator"));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(
            OrganizationRole.Owner,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_InactiveTarget_ReturnsNoStoreConflict()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member,
            targetMembershipActive: false);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"));

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_ExpectedCurrentRoleMismatch_ReturnsNoStoreConflict()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Administrator);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Member", "Member"));

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(
            OrganizationRole.Administrator,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_AlreadyAtRequestedFinalRole_ReturnsEmptyNoStoreNoContent()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Administrator);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"));

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(
            OrganizationRole.Administrator,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Theory]
    [InlineData("Owner", "Member")]
    [InlineData("Unsupported", "Member")]
    [InlineData("administrator", "Member")]
    [InlineData("Administrator", "Owner")]
    [InlineData("Administrator", "Unsupported")]
    [InlineData("Administrator", "member")]
    public async Task ChangeRole_InvalidRoleInput_ReturnsNoStoreBadRequest(
        string role,
        string expectedCurrentRole)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody(role, expectedCurrentRole));

        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangeRole_MissingRequiredField_ReturnsNoStoreBadRequest(
        bool omitRole)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);
        object body = omitRole
            ? new { expectedCurrentRole = "Member" }
            : new { role = "Administrator" };

        using HttpResponseMessage response = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_MalformedJson_ReturnsNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);
        using var request = CreateRoleRequest(
            graph,
            graph.ActorHandle,
            csrf,
            graph.TargetMembership.Id);
        request.Content = new StringContent(
            "{\"role\":",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ChangeRole_MissingOrInvalidAntiforgery_ReturnsEmptyNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage missing = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf: null,
            CreateBody("Administrator", "Member"));
        using HttpResponseMessage invalid = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("Administrator", "Member"),
            requestTokenOverride: "invalid-antiforgery-token");

        await AssertEmptyResponseAsync(missing, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(invalid, HttpStatusCode.BadRequest);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task Promotion_IsVisibleToSameSessionOnNextLiveAuthorization()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Member);
        CsrfPair ownerCsrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage before = await SendGetAsync(
            GetInactiveMembersPath(graph.Organization.Id),
            graph.TargetHandle);
        using HttpResponseMessage mutation = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            ownerCsrf,
            CreateBody("Administrator", "Member"));
        using HttpResponseMessage after = await SendGetAsync(
            GetInactiveMembersPath(graph.Organization.Id),
            graph.TargetHandle);

        await AssertEmptyResponseAsync(before, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(mutation, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.True(after.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Demotion_IsVisibleToSameSessionOnNextLiveAuthorization()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            OrganizationRole.Administrator);
        CsrfPair ownerCsrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage before = await SendGetAsync(
            GetInactiveMembersPath(graph.Organization.Id),
            graph.TargetHandle);
        using HttpResponseMessage mutation = await SendRoleAsync(
            graph,
            graph.ActorHandle,
            ownerCsrf,
            CreateBody("Member", "Administrator"));
        using HttpResponseMessage after = await SendGetAsync(
            GetInactiveMembersPath(graph.Organization.Id),
            graph.TargetHandle);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.True(before.Headers.CacheControl?.NoStore);
        await AssertEmptyResponseAsync(mutation, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(after, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RoleMutationServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();

        AssertScoped<ChangeOrganizationMemberRoleUseCase>(firstScope, secondScope);
        AssertScoped<IOrganizationMemberRoleMutationPersistence>(
            firstScope,
            secondScope);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole,
        OrganizationRole targetRole,
        bool targetMembershipActive = true)
    {
        Organization organization = CreateOrganization("Current");
        User actor = CreateUser("Actor");
        User target = CreateUser("Target");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            actorRole,
            Now.AddHours(-1));
        var targetMembership = new OrganizationMembership(
            organization.Id,
            target.Id,
            targetRole,
            Now.AddHours(-1));

        if (!targetMembershipActive)
        {
            targetMembership.Deactivate();
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

    private async Task<HttpResponseMessage> SendRoleAsync(
        TestGraph graph,
        string rawHandle,
        CsrfPair? csrf,
        object body,
        Guid? membershipId = null,
        string? requestTokenOverride = null)
    {
        using HttpRequestMessage request = CreateRoleRequest(
            graph,
            rawHandle,
            csrf,
            membershipId ?? graph.TargetMembership.Id,
            requestTokenOverride);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateRoleRequest(
        TestGraph graph,
        string rawHandle,
        CsrfPair? csrf,
        Guid membershipId,
        string? requestTokenOverride = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            GetRolePath(graph.Organization.Id, membershipId));
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

    private async Task<OrganizationRole> FindRoleAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.Role)
            .SingleAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static object CreateBody(string role, string expectedCurrentRole)
    {
        return new { role, expectedCurrentRole };
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
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
    }

    private static string GetRolePath(Guid organizationId, Guid membershipId)
    {
        return $"/api/organizations/{organizationId:D}/members/{membershipId:D}/role";
    }

    private static string GetInactiveMembersPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/members?status=inactive";
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
