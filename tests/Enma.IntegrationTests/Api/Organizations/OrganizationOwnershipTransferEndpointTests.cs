using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Members.Ownership;
using Enma.Domain.Auditing;
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
public sealed class OrganizationOwnershipTransferEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string CsrfPath = "/api/auth/csrf";
    private const string PasswordHash = "synthetic-ownership-transfer-password-hash";
    private const string TargetUnavailableCode = "ownership_transfer_target_unavailable";

    private static readonly DateTimeOffset Now = new(
        2026,
        10,
        5,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationOwnershipTransferEndpointTests(PostgreSqlFixture fixture)
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
    public void RequestContract_ContainsOnlyExpectedTargetRole()
    {
        Assert.Equal(
            [nameof(TransferOrganizationOwnershipRequest.ExpectedTargetRole)],
            typeof(TransferOrganizationOwnershipRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task Transfer_Anonymous_ReturnsEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetTransferPath(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(CreateBody("administrator"))
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Transfer_OwnerToActiveVerifiedAdministrator_SwapsRolesAndIsAudited()
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"));

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(
            OrganizationRole.Administrator,
            await FindRoleAsync(graph.ActorMembership.Id));
        Assert.Equal(
            OrganizationRole.Owner,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(1, await CountOwnersAsync(graph.Organization.Id));

        using HttpResponseMessage auditResponse = await SendGetAsync(
            $"/api/organizations/{graph.Organization.Id:D}/audit-logs" +
                "?eventType=organization.ownership_transferred",
            graph.TargetHandle);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using JsonDocument document = JsonDocument.Parse(
            await auditResponse.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            "organization.ownership_transferred",
            item.GetProperty("eventType").GetString());
        Assert.Equal("organization", item.GetProperty("entityType").GetString());
        Assert.Equal(graph.Organization.Id, item.GetProperty("entityId").GetGuid());
        Assert.Equal(
            graph.ActorMembership.Id,
            item.GetProperty("actorMembershipId").GetGuid());
        Assert.Equal("Owner", item.GetProperty("actorRoleAtOccurrence").GetString());
        JsonElement details = item.GetProperty("details");
        Assert.Equal(
            "organization.ownership_transferred",
            details.GetProperty("type").GetString());
        Assert.Equal(
            graph.ActorMembership.Id,
            details.GetProperty("previousOwnerMembershipId").GetGuid());
        Assert.Equal(
            graph.TargetMembership.Id,
            details.GetProperty("newOwnerMembershipId").GetGuid());
        Assert.Equal(
            ["newOwnerMembershipId", "previousOwnerMembershipId", "type"],
            details.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Transfer_FormerOwner_IsForbiddenOnNextOwnerOnlyAction()
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage transfer = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"));
        using HttpResponseMessage transferBack = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"));
        using HttpResponseMessage roleChange = await SendAsync(
            HttpMethod.Put,
            $"/api/organizations/{graph.Organization.Id:D}/members/" +
                $"{graph.TargetMembership.Id:D}/role",
            graph.ActorHandle,
            csrf,
            new { role = "Member", expectedCurrentRole = "Administrator" });

        await AssertEmptyResponseAsync(transfer, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(transferBack, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(roleChange, HttpStatusCode.Forbidden);
        Assert.Equal(
            OrganizationRole.Owner,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(1, await CountOwnershipTransferEventsAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task Transfer_NonOwnerActor_ReturnsEmptyNoStoreForbiddenWithoutMutation(
        OrganizationRole actorRole)
    {
        TestGraph graph = await SeedGraphAsync(actorRole: actorRole);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task Transfer_CrossTenantTarget_MatchesNonexistentNotFound()
    {
        TestGraph graph = await SeedGraphAsync();
        TestGraph foreign = await SeedGraphAsync("Foreign");
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage foreignResponse = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"),
            foreign.TargetMembership.Id);
        using HttpResponseMessage missingResponse = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"),
            Guid.NewGuid());

        await AssertEmptyResponseAsync(foreignResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
        await AssertUnchangedAsync(graph);
        await AssertUnchangedAsync(foreign);
    }

    [Fact]
    public async Task Transfer_ForeignOrganizationRoute_IsForbiddenWithoutMutation()
    {
        TestGraph graph = await SeedGraphAsync();
        TestGraph foreign = await SeedGraphAsync("Foreign");
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            GetTransferPath(foreign.Organization.Id, foreign.TargetMembership.Id),
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(foreign);
    }

    [Theory]
    [InlineData(TargetState.InactiveMembership)]
    [InlineData(TargetState.InactiveUser)]
    [InlineData(TargetState.UnverifiedEmail)]
    [InlineData(TargetState.Member)]
    [InlineData(TargetState.Self)]
    [InlineData(TargetState.ExpectedRoleMismatch)]
    public async Task Transfer_UnavailableTarget_ReturnsConflictWithStableCode(
        TargetState targetState)
    {
        TestGraph graph = await SeedGraphAsync(
            targetRole: targetState == TargetState.Member
                ? OrganizationRole.Member
                : OrganizationRole.Administrator,
            targetMembershipActive: targetState != TargetState.InactiveMembership,
            targetUserActive: targetState != TargetState.InactiveUser,
            targetEmailVerified: targetState != TargetState.UnverifiedEmail);
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody(targetState == TargetState.ExpectedRoleMismatch
                ? "member"
                : "administrator"),
            targetState == TargetState.Self
                ? graph.ActorMembership.Id
                : graph.TargetMembership.Id);

        await AssertTargetUnavailableProblemAsync(response);
        await AssertUnchangedAsync(graph);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("owner")]
    [InlineData("Owner")]
    [InlineData("")]
    [InlineData("unsupported")]
    public async Task Transfer_InvalidExpectedTargetRole_ReturnsNoStoreBadRequest(
        string expectedTargetRole)
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage response = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody(expectedTargetRole));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task Transfer_MissingOrNullExpectedTargetRole_ReturnsNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage missing = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            new { });
        using HttpResponseMessage nullValue = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            new { expectedTargetRole = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.BadRequest, nullValue.StatusCode);
        Assert.True(nullValue.Headers.CacheControl?.NoStore);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task Transfer_MalformedJson_ReturnsNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            GetTransferPath(graph.Organization.Id, graph.TargetMembership.Id),
            graph.ActorHandle,
            csrf);
        request.Content = new StringContent(
            "{\"expectedTargetRole\":",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task Transfer_MissingOrInvalidAntiforgery_ReturnsEmptyNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync();
        CsrfPair csrf = await GetCsrfPairAsync(graph.ActorHandle);

        using HttpResponseMessage missing = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf: null,
            CreateBody("administrator"));
        using HttpResponseMessage invalid = await SendTransferAsync(
            graph,
            graph.ActorHandle,
            csrf,
            CreateBody("administrator"),
            requestTokenOverride: "invalid-antiforgery-token");

        await AssertEmptyResponseAsync(missing, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(invalid, HttpStatusCode.BadRequest);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task OwnershipTransferServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();

        AssertScoped<TransferOrganizationOwnershipUseCase>(firstScope, secondScope);
        AssertScoped<IOrganizationOwnershipTransferPersistence>(
            firstScope,
            secondScope);
    }

    private async Task<TestGraph> SeedGraphAsync(
        string marker = "Current",
        OrganizationRole actorRole = OrganizationRole.Owner,
        OrganizationRole targetRole = OrganizationRole.Administrator,
        bool targetMembershipActive = true,
        bool targetUserActive = true,
        bool targetEmailVerified = true)
    {
        Organization organization = CreateOrganization(marker);
        User actor = CreateUser($"{marker} Actor");
        User target = CreateUser($"{marker} Target");
        actor.VerifyEmail(Now.AddHours(-2));

        if (targetEmailVerified)
        {
            target.VerifyEmail(Now.AddHours(-2));
        }

        if (!targetUserActive)
        {
            target.Deactivate();
        }

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

    private Task<HttpResponseMessage> SendTransferAsync(
        TestGraph graph,
        string rawHandle,
        CsrfPair? csrf,
        object body,
        Guid? membershipId = null,
        string? requestTokenOverride = null)
    {
        return SendAsync(
            HttpMethod.Post,
            GetTransferPath(
                graph.Organization.Id,
                membershipId ?? graph.TargetMembership.Id),
            rawHandle,
            csrf,
            body,
            requestTokenOverride);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair? csrf,
        object body,
        string? requestTokenOverride = null)
    {
        using HttpRequestMessage request = CreateRequest(
            method,
            path,
            rawHandle,
            csrf,
            requestTokenOverride);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair? csrf,
        string? requestTokenOverride = null)
    {
        var request = new HttpRequestMessage(method, path);
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

    private async Task AssertUnchangedAsync(TestGraph graph)
    {
        Assert.Equal(
            graph.ActorMembership.Role,
            await FindRoleAsync(graph.ActorMembership.Id));
        Assert.Equal(
            graph.TargetMembership.Role,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountOwnershipTransferEventsAsync());
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

    private async Task<int> CountOwnersAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships.CountAsync(membership =>
            membership.OrganizationId == organizationId &&
            membership.Role == OrganizationRole.Owner &&
            membership.IsActive);
    }

    private async Task<int> CountOwnershipTransferEventsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.CountAsync(auditLog =>
            auditLog.EventType == AuditEventType.OrganizationOwnershipTransferred);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static object CreateBody(string expectedTargetRole)
    {
        return new { expectedTargetRole };
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

    private static string GetTransferPath(Guid organizationId, Guid membershipId)
    {
        return $"/api/organizations/{organizationId:D}/members/" +
            $"{membershipId:D}/transfer-ownership";
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

    private static async Task AssertTargetUnavailableProblemAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(content);
        Assert.Equal(
            TargetUnavailableCode,
            document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("System.", content);
        Assert.DoesNotContain("stackTrace", content);
        Assert.DoesNotContain("organizationId", content);
        Assert.DoesNotContain("membershipId", content);
        Assert.DoesNotContain("@example.test", content);
    }

    public enum TargetState
    {
        InactiveMembership = 0,
        InactiveUser = 1,
        UnverifiedEmail = 2,
        Member = 3,
        Self = 4,
        ExpectedRoleMismatch = 5
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
