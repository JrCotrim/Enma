using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.List;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberAdministrationEndpointTests
    : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-team-administration-password-hash";

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

    public OrganizationMemberAdministrationEndpointTests(PostgreSqlFixture fixture)
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
    public async Task List_Anonymous_ReturnsEmptyNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetPath(Guid.Parse("57315f57-7647-4f78-a3a6-52a0af349bf2")));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_Member_SeesOnlyEffectiveActiveNameProjectionAndNameSearch()
    {
        Organization organization = CreateOrganization("Member Current");
        Organization foreignOrganization = CreateOrganization("Member Foreign");
        User caller = CreateUser("Member Caller", "member.caller@example.test");
        User active = CreateUser("Visible Member", "visible.member@example.test");
        User inactiveUser = CreateUser(
            "Inactive Account",
            "inactive.account.member@example.test");
        inactiveUser.Deactivate();
        User inactiveMembershipUser = CreateUser(
            "Inactive Membership",
            "inactive.membership.member@example.test");
        User foreign = CreateUser("Foreign Only", "foreign-only@example.test");
        OrganizationMembership callerMembership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Member);
        OrganizationMembership activeMembership = CreateMembership(
            organization,
            active,
            OrganizationRole.Administrator);
        OrganizationMembership inactiveUserMembership = CreateMembership(
            organization,
            inactiveUser,
            OrganizationRole.Member);
        OrganizationMembership inactiveMembership = CreateMembership(
            organization,
            inactiveMembershipUser,
            OrganizationRole.Member);
        inactiveMembership.Deactivate();
        OrganizationMembership foreignMembership = CreateMembership(
            foreignOrganization,
            foreign,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization, foreignOrganization],
            [caller, active, inactiveUser, inactiveMembershipUser, foreign],
            [
                callerMembership,
                activeMembership,
                inactiveUserMembership,
                inactiveMembership,
                foreignMembership
            ]);

        using HttpResponseMessage defaultResponse = await SendAsync(
            GetPath(organization.Id),
            rawHandle);
        using HttpResponseMessage explicitActiveResponse = await SendAsync(
            $"{GetPath(organization.Id)}?status=active",
            rawHandle);
        using HttpResponseMessage nameSearchResponse = await SendAsync(
            $"{GetPath(organization.Id)}?search=visible%20member",
            rawHandle);
        using HttpResponseMessage emailSearchResponse = await SendAsync(
            $"{GetPath(organization.Id)}?search=visible.member%40example.test",
            rawHandle);
        using HttpResponseMessage foreignSearchResponse = await SendAsync(
            $"{GetPath(organization.Id)}?search=foreign%20only",
            rawHandle);

        ListOrganizationMembersResponse defaultPage = await ReadPageAsync(
            defaultResponse);
        ListOrganizationMembersResponse explicitActivePage = await ReadPageAsync(
            explicitActiveResponse);
        ListOrganizationMembersResponse nameSearchPage = await ReadPageAsync(
            nameSearchResponse);
        ListOrganizationMembersResponse emailSearchPage = await ReadPageAsync(
            emailSearchResponse);
        ListOrganizationMembersResponse foreignSearchPage = await ReadPageAsync(
            foreignSearchResponse);

        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.True(defaultResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(2, defaultPage.TotalCount);
        Assert.Equal(2, explicitActivePage.TotalCount);
        Assert.Equal(
            new[] { callerMembership.Id, activeMembership.Id }.OrderBy(id => id),
            defaultPage.Items.Select(item => item.Id).OrderBy(id => id));
        Assert.DoesNotContain(
            defaultPage.Items,
            item => item.Id == inactiveUserMembership.Id ||
                item.Id == inactiveMembership.Id ||
                item.Id == foreignMembership.Id);
        OrganizationMemberResponse visible = Assert.Single(nameSearchPage.Items);
        Assert.Equal(activeMembership.Id, visible.Id);
        Assert.Equal("Administrator", visible.Role);
        Assert.Null(visible.Email);
        Assert.Null(visible.MembershipStatus);
        Assert.Null(visible.AccountStatus);
        Assert.Empty(emailSearchPage.Items);
        Assert.Equal(0, emailSearchPage.TotalCount);
        Assert.Empty(foreignSearchPage.Items);
        Assert.Equal(0, foreignSearchPage.TotalCount);

        string json = await nameSearchResponse.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            ["id", "name", "role"],
            document.RootElement
                .GetProperty("items")[0]
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "organizationId",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_MemberRequestingInactive_ReturnsEmptyNoStoreForbidden()
    {
        Organization organization = CreateOrganization("Member Denied");
        User caller = CreateUser("Member Denied", "member.denied@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(organization.Id)}?status=inactive",
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_Administrator_SeesMembershipAndAccountStatesSeparately()
    {
        Organization organization = CreateOrganization("Administrator Current");
        User caller = CreateUser(
            "Administrator Caller",
            "administrator.caller@example.test");
        User inactiveAccount = CreateUser(
            "Inactive Account",
            "administrator.inactive.account@example.test");
        inactiveAccount.Deactivate();
        User inactiveMembershipActiveAccount = CreateUser(
            "Inactive Membership Active Account",
            "administrator.inactive.membership.active@example.test");
        User bothInactive = CreateUser(
            "Both Inactive",
            "administrator.both.inactive@example.test");
        bothInactive.Deactivate();
        OrganizationMembership callerMembership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Administrator);
        OrganizationMembership inactiveAccountMembership = CreateMembership(
            organization,
            inactiveAccount,
            OrganizationRole.Member);
        OrganizationMembership inactiveMembership = CreateMembership(
            organization,
            inactiveMembershipActiveAccount,
            OrganizationRole.Member);
        inactiveMembership.Deactivate();
        OrganizationMembership bothInactiveMembership = CreateMembership(
            organization,
            bothInactive,
            OrganizationRole.Member);
        bothInactiveMembership.Deactivate();
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller, inactiveAccount, inactiveMembershipActiveAccount, bothInactive],
            [
                callerMembership,
                inactiveAccountMembership,
                inactiveMembership,
                bothInactiveMembership
            ]);

        using HttpResponseMessage activeResponse = await SendAsync(
            $"{GetPath(organization.Id)}?status=active",
            rawHandle);
        using HttpResponseMessage inactiveResponse = await SendAsync(
            $"{GetPath(organization.Id)}?status=inactive",
            rawHandle);
        ListOrganizationMembersResponse active = await ReadPageAsync(activeResponse);
        ListOrganizationMembersResponse inactive = await ReadPageAsync(
            inactiveResponse);

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        Assert.Equal(2, active.TotalCount);
        OrganizationMemberResponse inactiveAccountItem = Assert.Single(
            active.Items,
            item => item.Id == inactiveAccountMembership.Id);
        Assert.Equal(inactiveAccount.Email, inactiveAccountItem.Email);
        Assert.Equal("Active", inactiveAccountItem.MembershipStatus);
        Assert.Equal("Inactive", inactiveAccountItem.AccountStatus);

        Assert.Equal(HttpStatusCode.OK, inactiveResponse.StatusCode);
        Assert.True(inactiveResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(2, inactive.TotalCount);
        OrganizationMemberResponse activeAccountItem = Assert.Single(
            inactive.Items,
            item => item.Id == inactiveMembership.Id);
        OrganizationMemberResponse bothInactiveItem = Assert.Single(
            inactive.Items,
            item => item.Id == bothInactiveMembership.Id);
        Assert.Equal("Inactive", activeAccountItem.MembershipStatus);
        Assert.Equal("Active", activeAccountItem.AccountStatus);
        Assert.Equal("Inactive", bothInactiveItem.MembershipStatus);
        Assert.Equal("Inactive", bothInactiveItem.AccountStatus);
    }

    [Fact]
    public async Task List_Owner_HasSamePrivilegedReadVisibilityAsAdministrator()
    {
        Organization organization = CreateOrganization("Owner Current");
        User caller = CreateUser("Owner Caller", "owner.caller@example.test");
        User inactiveMember = CreateUser(
            "Owner Inactive Membership",
            "owner.inactive.membership@example.test");
        OrganizationMembership callerMembership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        OrganizationMembership inactiveMembership = CreateMembership(
            organization,
            inactiveMember,
            OrganizationRole.Member);
        inactiveMembership.Deactivate();
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller, inactiveMember],
            [callerMembership, inactiveMembership]);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(organization.Id)}?status=inactive&search={Uri.EscapeDataString(inactiveMember.Email.ToUpperInvariant())}",
            rawHandle);
        ListOrganizationMembersResponse page = await ReadPageAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OrganizationMemberResponse item = Assert.Single(page.Items);
        Assert.Equal(inactiveMembership.Id, item.Id);
        Assert.Equal(inactiveMember.Email, item.Email);
        Assert.Equal("Inactive", item.MembershipStatus);
        Assert.Equal("Active", item.AccountStatus);
    }

    [Fact]
    public async Task List_PageBeyondRows_ReturnsNoStoreOkEmptyPageWithStableTotal()
    {
        Organization organization = CreateOrganization("Beyond Page");
        User caller = CreateUser("Beyond Caller", "beyond.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(organization.Id)}?pageNumber=2&pageSize=20",
            rawHandle);
        ListOrganizationMembersResponse page = await ReadPageAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(20, page.PageSize);
    }

    [Theory]
    [InlineData("status=pending")]
    [InlineData("pageNumber=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageNumber=not-a-number")]
    public async Task List_InvalidQuery_ReturnsNoStoreBadRequest(string query)
    {
        Organization organization = CreateOrganization("Invalid Query");
        User caller = CreateUser("Invalid Caller", "invalid.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(organization.Id)}?{query}",
            rawHandle);

        if (query == "pageNumber=not-a-number")
        {
            await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
        }
        else
        {
            await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task List_ExcessiveSearch_ReturnsNoStoreBadRequest()
    {
        Organization organization = CreateOrganization("Invalid Search");
        User caller = CreateUser(
            "Invalid Search Caller",
            "invalid.search.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(organization.Id)}?search={new string('x', 151)}",
            rawHandle);

        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_InaccessibleOrganization_ReturnsEmptyNoStoreForbidden()
    {
        Organization accessible = CreateOrganization("Accessible");
        Organization inaccessible = CreateOrganization("Inaccessible");
        User caller = CreateUser("Access Caller", "access.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            accessible,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [accessible, inaccessible],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(inaccessible.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_InactiveActorMembership_ReturnsEmptyNoStoreForbidden()
    {
        Organization organization = CreateOrganization("Inactive Actor Membership");
        User caller = CreateUser(
            "Inactive Membership Caller",
            "inactive.membership.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        membership.Deactivate();
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_InactiveOrganization_ReturnsEmptyNoStoreForbidden()
    {
        Organization organization = CreateOrganization("Inactive Organization");
        organization.Deactivate();
        User caller = CreateUser(
            "Inactive Organization Caller",
            "inactive.organization.caller@example.test");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_InactiveActorUser_ReturnsEmptyNoStoreUnauthorized()
    {
        Organization organization = CreateOrganization("Inactive Actor User");
        User caller = CreateUser(
            "Inactive User Caller",
            "inactive.user.caller@example.test");
        caller.Deactivate();
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TeamAdministrationReadServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();

        AssertScoped<OrganizationAdministrationAuthorization>(
            firstScope,
            secondScope);
        AssertScoped<ListOrganizationMembersUseCase>(firstScope, secondScope);
        AssertScoped<IOrganizationMemberAdministrationQueries>(
            firstScope,
            secondScope);
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

    private async Task<string> SeedAuthenticatedCallerAsync(
        User caller,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<OrganizationMembership> memberships)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            caller.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            caller.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.AddRange(users);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static async Task<ListOrganizationMembersResponse> ReadPageAsync(
        HttpResponseMessage response)
    {
        ListOrganizationMembersResponse? page = await response.Content
            .ReadFromJsonAsync<ListOrganizationMembersResponse>();
        return Assert.IsType<ListOrganizationMembersResponse>(page);
    }

    private static User CreateUser(string name, string email)
    {
        return new User(name, email, Now.AddHours(-2));
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
    }

    private static OrganizationMembership CreateMembership(
        Organization organization,
        User user,
        OrganizationRole role)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1));
    }

    private static string GetPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/members";
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
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
