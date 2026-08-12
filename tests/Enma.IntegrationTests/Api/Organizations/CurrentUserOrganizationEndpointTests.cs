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
public sealed class CurrentUserOrganizationEndpointTests : IAsyncLifetime
{
    private const string RequestPath = "/api/me/organizations";
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash =
        "synthetic-current-user-organizations-password-hash";

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

    public CurrentUserOrganizationEndpointTests(PostgreSqlFixture fixture)
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
    public void ResponseContracts_CurrentScope_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [nameof(GetCurrentUserOrganizationsResponse.Items)],
            GetPropertyNames<GetCurrentUserOrganizationsResponse>());
        Assert.Equal(
            [
                nameof(CurrentUserOrganizationResponse.Id),
                nameof(CurrentUserOrganizationResponse.Name),
                nameof(CurrentUserOrganizationResponse.Role)
            ],
            GetPropertyNames<CurrentUserOrganizationResponse>());
    }

    [Fact]
    public async Task Get_Anonymous_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(RequestPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_AuthenticatedWithoutMemberships_ReturnsNoStoreEmptyItemsWithoutCsrf()
    {
        User user = CreateUser("Current User", "empty@example.test");
        string rawHandle = await SeedAuthenticatedUserAsync(user, [], []);

        using HttpResponseMessage response = await SendAsync(rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        GetCurrentUserOrganizationsResponse? result =
            await response.Content
                .ReadFromJsonAsync<GetCurrentUserOrganizationsResponse>();
        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Get_TwoAuthenticatedUsers_ReturnsOnlyEachUsersOrganizationsWithContextualRoleStrings()
    {
        User firstUser = CreateUser("First User", "first@example.test");
        User secondUser = CreateUser("Second User", "second@example.test");
        Organization alpha = CreateOrganization("Alpha Legal", "alpha-legal");
        Organization zeta = CreateOrganization("Zeta Legal", "zeta-legal");
        Organization second = CreateOrganization("Second Legal", "second-legal");
        OrganizationMembership alphaMembership = CreateMembership(
            firstUser,
            alpha,
            OrganizationRole.Member);
        OrganizationMembership zetaMembership = CreateMembership(
            firstUser,
            zeta,
            OrganizationRole.Owner);
        OrganizationMembership secondMembership = CreateMembership(
            secondUser,
            second,
            OrganizationRole.Administrator);
        string firstHandle = await SeedAuthenticatedUserAsync(
            firstUser,
            [alpha, zeta],
            [alphaMembership, zetaMembership]);
        string secondHandle = await SeedAuthenticatedUserAsync(
            secondUser,
            [second],
            [secondMembership]);

        using HttpResponseMessage firstResponse = await SendAsync(firstHandle);
        using HttpResponseMessage secondResponse = await SendAsync(secondHandle);

        GetCurrentUserOrganizationsResponse firstResult =
            await ReadSuccessfulResponseAsync(firstResponse);
        GetCurrentUserOrganizationsResponse secondResult =
            await ReadSuccessfulResponseAsync(secondResponse);
        Assert.Collection(
            firstResult.Items,
            item => AssertOrganization(item, alpha, "Member"),
            item => AssertOrganization(item, zeta, "Owner"));
        CurrentUserOrganizationResponse secondItem = Assert.Single(
            secondResult.Items);
        AssertOrganization(secondItem, second, "Administrator");
        string firstJson = await firstResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"role\":\"Member\"", firstJson);
        Assert.Contains("\"role\":\"Owner\"", firstJson);
        Assert.DoesNotContain("\"role\":1", firstJson);
        Assert.DoesNotContain("\"role\":2", firstJson);
        Assert.DoesNotContain("\"role\":3", firstJson);
    }

    [Fact]
    public async Task Get_WithInactiveMembershipAndOrganization_ExcludesBoth()
    {
        User user = CreateUser("Current User", "current@example.test");
        Organization active = CreateOrganization("Active Legal", "active-legal");
        Organization inactiveMembershipOrganization = CreateOrganization(
            "Inactive Membership Legal",
            "inactive-membership-legal");
        Organization inactiveOrganization = CreateOrganization(
            "Inactive Organization Legal",
            "inactive-organization-legal");
        inactiveOrganization.Deactivate();
        OrganizationMembership activeMembership = CreateMembership(
            user,
            active,
            OrganizationRole.Administrator);
        OrganizationMembership inactiveMembership = CreateMembership(
            user,
            inactiveMembershipOrganization,
            OrganizationRole.Owner);
        inactiveMembership.Deactivate();
        OrganizationMembership inactiveOrganizationMembership = CreateMembership(
            user,
            inactiveOrganization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [active, inactiveMembershipOrganization, inactiveOrganization],
            [
                activeMembership,
                inactiveMembership,
                inactiveOrganizationMembership
            ]);

        using HttpResponseMessage response = await SendAsync(rawHandle);

        GetCurrentUserOrganizationsResponse result =
            await ReadSuccessfulResponseAsync(response);
        CurrentUserOrganizationResponse item = Assert.Single(result.Items);
        AssertOrganization(item, active, "Administrator");
    }

    [Fact]
    public async Task Get_AfterLiveRoleMembershipAndOrganizationChanges_ReflectsCurrentStateWithoutRelogin()
    {
        User user = CreateUser("Current User", "current@example.test");
        Organization organization = CreateOrganization(
            "Current Legal",
            "current-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership]);

        using HttpResponseMessage initialResponse = await SendAsync(rawHandle);
        GetCurrentUserOrganizationsResponse initial =
            await ReadSuccessfulResponseAsync(initialResponse);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.ChangeRole(OrganizationRole.Administrator);
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage changedResponse = await SendAsync(rawHandle);
        GetCurrentUserOrganizationsResponse changed =
            await ReadSuccessfulResponseAsync(changedResponse);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage membershipInactiveResponse =
            await SendAsync(rawHandle);
        GetCurrentUserOrganizationsResponse membershipInactive =
            await ReadSuccessfulResponseAsync(membershipInactiveResponse);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.Activate();
            Organization persistedOrganization =
                await mutationContext.Organizations.SingleAsync();
            persistedOrganization.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage organizationInactiveResponse =
            await SendAsync(rawHandle);
        GetCurrentUserOrganizationsResponse organizationInactive =
            await ReadSuccessfulResponseAsync(organizationInactiveResponse);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            Organization persistedOrganization =
                await mutationContext.Organizations.SingleAsync();
            persistedOrganization.Activate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage reactivatedResponse = await SendAsync(rawHandle);
        GetCurrentUserOrganizationsResponse reactivated =
            await ReadSuccessfulResponseAsync(reactivatedResponse);

        Assert.Equal("Member", Assert.Single(initial.Items).Role);
        Assert.Equal("Administrator", Assert.Single(changed.Items).Role);
        Assert.Empty(membershipInactive.Items);
        Assert.Empty(organizationInactive.Items);
        Assert.Equal("Administrator", Assert.Single(reactivated.Items).Role);
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

    private async Task<HttpResponseMessage> SendAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestPath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static async Task<GetCurrentUserOrganizationsResponse>
        ReadSuccessfulResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        GetCurrentUserOrganizationsResponse? result =
            await response.Content
                .ReadFromJsonAsync<GetCurrentUserOrganizationsResponse>();
        return Assert.IsType<GetCurrentUserOrganizationsResponse>(result);
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

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
    }

    private static void AssertOrganization(
        CurrentUserOrganizationResponse item,
        Organization organization,
        string role)
    {
        Assert.Equal(organization.Id, item.Id);
        Assert.Equal(organization.Name, item.Name);
        Assert.Equal(role, item.Role);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
