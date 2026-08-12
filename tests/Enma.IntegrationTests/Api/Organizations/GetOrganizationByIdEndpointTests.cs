using System.Net;
using System.Net.Http.Json;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
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
public sealed class GetOrganizationByIdEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash =
        "synthetic-organization-metadata-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        12,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public GetOrganizationByIdEndpointTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

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
    public void ResponseContract_CurrentScope_ExposesOnlyApprovedMetadata()
    {
        Assert.Equal(
            [
                nameof(GetOrganizationResponse.Id),
                nameof(GetOrganizationResponse.Name),
                nameof(GetOrganizationResponse.Slug),
                nameof(GetOrganizationResponse.IsActive),
                nameof(GetOrganizationResponse.CreatedAt)
            ],
            typeof(GetOrganizationResponse)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task Get_Anonymous_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            CreateRequestPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains(HeaderNames.WWWAuthenticate));
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ActiveMemberWithoutCsrf_ReturnsNoStoreOrganizationMetadata()
    {
        User user = CreateUser("Organization Member", "member@example.test");
        Organization organization = CreateOrganization(
            "Enma Legal",
            "enma-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            rawHandle,
            organization.Id);

        await AssertSuccessfulResponseAsync(response, organization);
    }

    [Fact]
    public async Task Get_InaccessibleAndMissingOrganizations_ReturnSameNoStoreForbidden()
    {
        User user = CreateUser("No Access User", "no-access@example.test");
        Organization inaccessible = CreateOrganization(
            "Inaccessible Legal",
            "inaccessible-legal");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [inaccessible],
            []);

        using HttpResponseMessage inaccessibleResponse = await SendAsync(
            rawHandle,
            inaccessible.Id);
        using HttpResponseMessage missingResponse = await SendAsync(
            rawHandle,
            Guid.NewGuid());

        string inaccessibleBody = await AssertForbiddenAsync(
            inaccessibleResponse);
        string missingBody = await AssertForbiddenAsync(missingResponse);
        Assert.Equal(inaccessibleBody, missingBody);
        Assert.DoesNotContain(inaccessible.Name, inaccessibleBody);
        Assert.DoesNotContain(inaccessible.Slug, inaccessibleBody);
    }

    [Fact]
    public async Task Get_SeparateUsersAndOrganizations_EnforcesCrossTenantIsolation()
    {
        User firstUser = CreateUser("First User", "first@example.test");
        User secondUser = CreateUser("Second User", "second@example.test");
        Organization firstOrganization = CreateOrganization(
            "First Legal",
            "first-legal");
        Organization secondOrganization = CreateOrganization(
            "Second Legal",
            "second-legal");
        string firstHandle = await SeedAuthenticatedUserAsync(
            firstUser,
            [firstOrganization],
            [CreateMembership(
                firstUser,
                firstOrganization,
                OrganizationRole.Owner)]);
        string secondHandle = await SeedAuthenticatedUserAsync(
            secondUser,
            [secondOrganization],
            [CreateMembership(
                secondUser,
                secondOrganization,
                OrganizationRole.Administrator)]);

        using HttpResponseMessage firstOwnResponse = await SendAsync(
            firstHandle,
            firstOrganization.Id);
        using HttpResponseMessage firstCrossTenantResponse = await SendAsync(
            firstHandle,
            secondOrganization.Id);
        using HttpResponseMessage secondOwnResponse = await SendAsync(
            secondHandle,
            secondOrganization.Id);

        await AssertSuccessfulResponseAsync(
            firstOwnResponse,
            firstOrganization);
        string deniedBody = await AssertForbiddenAsync(
            firstCrossTenantResponse);
        Assert.DoesNotContain(secondOrganization.Name, deniedBody);
        Assert.DoesNotContain(secondOrganization.Slug, deniedBody);
        await AssertSuccessfulResponseAsync(
            secondOwnResponse,
            secondOrganization);
    }

    [Fact]
    public async Task Get_DualMembership_ReturnsMetadataSelectedByRouteContext()
    {
        User user = CreateUser("Dual Member", "dual@example.test");
        Organization firstOrganization = CreateOrganization(
            "Alpha Legal",
            "alpha-legal");
        Organization secondOrganization = CreateOrganization(
            "Beta Legal",
            "beta-legal");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [firstOrganization, secondOrganization],
            [
                CreateMembership(
                    user,
                    firstOrganization,
                    OrganizationRole.Member),
                CreateMembership(
                    user,
                    secondOrganization,
                    OrganizationRole.Owner)
            ]);

        using HttpResponseMessage firstResponse = await SendAsync(
            rawHandle,
            firstOrganization.Id);
        using HttpResponseMessage secondResponse = await SendAsync(
            rawHandle,
            secondOrganization.Id);

        await AssertSuccessfulResponseAsync(firstResponse, firstOrganization);
        await AssertSuccessfulResponseAsync(secondResponse, secondOrganization);
    }

    [Fact]
    public async Task Get_MembershipLifecycleChangesWithoutRelogin_UsesLiveState()
    {
        User user = CreateUser("Lifecycle Member", "membership@example.test");
        Organization organization = CreateOrganization(
            "Membership Legal",
            "membership-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership]);

        using HttpResponseMessage initialResponse = await SendAsync(
            rawHandle,
            organization.Id);
        await AssertSuccessfulResponseAsync(initialResponse, organization);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await mutationContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membership.Id);
            persistedMembership.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage deactivatedResponse = await SendAsync(
            rawHandle,
            organization.Id);
        await AssertForbiddenAsync(deactivatedResponse);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await mutationContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membership.Id);
            persistedMembership.Activate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage reactivatedResponse = await SendAsync(
            rawHandle,
            organization.Id);
        await AssertSuccessfulResponseAsync(reactivatedResponse, organization);
    }

    [Fact]
    public async Task Get_OrganizationLifecycleChangesWithoutRelogin_UsesLiveState()
    {
        User user = CreateUser("Organization Lifecycle Member", "organization@example.test");
        Organization organization = CreateOrganization(
            "Lifecycle Legal",
            "lifecycle-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Administrator);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership]);

        using HttpResponseMessage initialResponse = await SendAsync(
            rawHandle,
            organization.Id);
        await AssertSuccessfulResponseAsync(initialResponse, organization);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            Organization persistedOrganization = await mutationContext
                .Organizations
                .SingleAsync(candidate => candidate.Id == organization.Id);
            persistedOrganization.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage deactivatedResponse = await SendAsync(
            rawHandle,
            organization.Id);
        await AssertForbiddenAsync(deactivatedResponse);
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships)
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

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string rawHandle,
        Guid organizationId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CreateRequestPath(organizationId));
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static async Task<GetOrganizationResponse>
        AssertSuccessfulResponseAsync(
            HttpResponseMessage response,
            Organization organization)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);
        GetOrganizationResponse? result = await response.Content
            .ReadFromJsonAsync<GetOrganizationResponse>();
        GetOrganizationResponse metadata = Assert.IsType<
            GetOrganizationResponse>(result);
        Assert.Equal(organization.Id, metadata.Id);
        Assert.Equal(organization.Name, metadata.Name);
        Assert.Equal(organization.Slug, metadata.Slug);
        Assert.Equal(organization.IsActive, metadata.IsActive);
        Assert.Equal(organization.CreatedAt, metadata.CreatedAt);

        return metadata;
    }

    private static async Task<string> AssertForbiddenAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains(HeaderNames.WWWAuthenticate));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);

        return body;
    }

    private static string CreateRequestPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}";
    }

    private static User CreateUser(string name, string email)
    {
        return new User(name, email, Now.AddHours(-2));
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, Now.AddHours(-2));
    }

    private static OrganizationMembership CreateMembership(
        User user,
        Organization organization,
        OrganizationRole role)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
