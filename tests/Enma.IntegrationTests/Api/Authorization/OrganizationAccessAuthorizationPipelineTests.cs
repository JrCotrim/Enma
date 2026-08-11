using System.Net;
using System.Security.Claims;
using Enma.Api.Authorization;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Authorization;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationAccessAuthorizationPipelineTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-authorization-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        20,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationAccessAuthorizationPipelineTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IStartupFilter, OrganizationAccessProbeStartupFilter>();
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task GetOrganizationAccess_AnonymousRequest_ReturnsGenericUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetProbePath(Guid.Parse("b0c27984-9e98-43ad-a3b3-e280229b17c7")));

        await AssertDeniedResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OrganizationAccessPolicy_Registration_IsExplicitAndHandlerIsScoped()
    {
        IAuthorizationPolicyProvider policyProvider = factory.Services
            .GetRequiredService<IAuthorizationPolicyProvider>();
        AuthorizationPolicy? policy = await policyProvider.GetPolicyAsync(
            EnmaAuthorizationPolicies.OrganizationAccess);

        Assert.NotNull(policy);
        Assert.Contains(
            policy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
        Assert.Contains(
            policy.Requirements,
            requirement => requirement is OrganizationAccessRequirement);
        Assert.Null(await policyProvider.GetFallbackPolicyAsync());

        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();
        IAuthorizationHandler firstHandler = Assert.Single(
            firstScope.ServiceProvider
                .GetServices<IAuthorizationHandler>(),
            handler => handler is OrganizationAccessAuthorizationHandler);
        IAuthorizationHandler secondHandler = Assert.Single(
            secondScope.ServiceProvider
                .GetServices<IAuthorizationHandler>(),
            handler => handler is OrganizationAccessAuthorizationHandler);

        Assert.Same(
            firstHandler,
            firstScope.ServiceProvider
                .GetServices<IAuthorizationHandler>()
                .Single(handler => handler is OrganizationAccessAuthorizationHandler));
        Assert.NotSame(firstHandler, secondHandler);
    }

    [Fact]
    public async Task GetOrganizationAccess_ActiveMembershipAndOrganization_ExecutesEndpointWithMinimalPrincipal()
    {
        User user = CreateUser("Allowed User", "allowed@example.test");
        Organization organization = CreateOrganization("Allowed Legal", "allowed-legal");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            Now.AddHours(-2));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership);

        using HttpResponseMessage response = await GetProbeAsync(
            organization.Id,
            rawHandle);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetOrganizationAccess_DifferentUsersMembershipInTargetOrganization_ReturnsGenericForbidden()
    {
        User firstUser = CreateUser("First User", "first@example.test");
        User secondUser = CreateUser("Second User", "second@example.test");
        Organization firstOrganization = CreateOrganization(
            "First Legal",
            "first-legal");
        Organization secondOrganization = CreateOrganization(
            "Second Legal",
            "second-legal");
        var firstMembership = new OrganizationMembership(
            firstOrganization.Id,
            firstUser.Id,
            OrganizationRole.Owner,
            Now.AddHours(-2));
        var secondMembership = new OrganizationMembership(
            secondOrganization.Id,
            secondUser.Id,
            OrganizationRole.Administrator,
            Now.AddHours(-2));
        string rawHandle = await SeedAuthenticatedUserAsync(
            firstUser,
            firstOrganization,
            secondOrganization,
            secondUser,
            firstMembership,
            secondMembership);

        using HttpResponseMessage response = await GetProbeAsync(
            secondOrganization.Id,
            rawHandle);

        await AssertDeniedResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOrganizationAccess_ReactivatedMembershipWithoutRelogin_UsesLiveMembershipState()
    {
        User user = CreateUser("Membership User", "membership@example.test");
        Organization organization = CreateOrganization(
            "Membership Legal",
            "membership-legal");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            Now.AddHours(-2));
        membership.Deactivate();
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership);

        using HttpResponseMessage deniedResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        await AssertDeniedResponseAsync(deniedResponse, HttpStatusCode.Forbidden);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await dbContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membership.Id);
            persistedMembership.Activate();
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage allowedResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
    }

    [Fact]
    public async Task GetOrganizationAccess_DeactivatedOrganizationWithoutRelogin_UsesLiveOrganizationState()
    {
        User user = CreateUser("Organization User", "organization@example.test");
        Organization organization = CreateOrganization(
            "Organization Legal",
            "organization-legal");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            Now.AddHours(-2));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership);

        using HttpResponseMessage allowedResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            Organization persistedOrganization = await dbContext.Organizations
                .SingleAsync(candidate => candidate.Id == organization.Id);
            persistedOrganization.Deactivate();
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage deniedResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        await AssertDeniedResponseAsync(deniedResponse, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOrganizationAccess_RoleChangedWithoutRelogin_RemainsAuthorizedWithoutRoleClaim()
    {
        User user = CreateUser("Role User", "role@example.test");
        Organization organization = CreateOrganization("Role Legal", "role-legal");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            Now.AddHours(-2));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership);

        using HttpResponseMessage ownerResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        Assert.Equal(HttpStatusCode.NoContent, ownerResponse.StatusCode);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await dbContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membership.Id);
            persistedMembership.ChangeRole(OrganizationRole.Member);
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage memberResponse = await GetProbeAsync(
            organization.Id,
            rawHandle);
        Assert.Equal(HttpStatusCode.NoContent, memberResponse.StatusCode);
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        params object[] additionalEntities)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-2));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(5),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(user);
        dbContext.Add(credential);
        dbContext.Add(session);
        dbContext.AddRange(additionalEntities);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<HttpResponseMessage> GetProbeAsync(
        Guid organizationId,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            GetProbePath(organizationId));
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static string GetProbePath(Guid organizationId)
    {
        return $"/_test/organizations/{organizationId:D}/access";
    }

    private static async Task AssertDeniedResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.False(response.Headers.Contains(HeaderNames.WWWAuthenticate));
    }

    private static User CreateUser(string name, string email)
    {
        var user = new User(name, email, Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        return user;
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, Now.AddHours(-2));
    }

    private sealed class OrganizationAccessProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(
            Action<IApplicationBuilder> next)
        {
            return application =>
            {
                next(application);
                application.UseEndpoints(endpoints =>
                {
                    endpoints
                        .MapGet(
                            "/_test/organizations/{organizationId:guid}/access",
                            (HttpContext context) =>
                            {
                                Claim[] claims = context.User.Claims.ToArray();
                                bool hasMinimalPrincipal =
                                    context.User.Identity?.IsAuthenticated == true &&
                                    claims.Length == 1 &&
                                    claims[0].Type == ClaimTypes.NameIdentifier &&
                                    Guid.TryParseExact(claims[0].Value, "D", out Guid userId) &&
                                    userId != Guid.Empty;

                                return hasMinimalPrincipal
                                    ? Results.NoContent()
                                    : Results.StatusCode(StatusCodes.Status500InternalServerError);
                            })
                        .RequireAuthorization(
                            EnmaAuthorizationPolicies.OrganizationAccess);
                });
            };
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
