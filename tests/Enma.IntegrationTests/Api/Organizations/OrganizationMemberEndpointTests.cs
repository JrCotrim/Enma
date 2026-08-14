using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
public sealed class OrganizationMemberEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-member-lookup-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        14,
        20,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationMemberEndpointTests(PostgreSqlFixture fixture)
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
    public void OrganizationMemberLookupContracts_ExposeOnlyMembershipIdAndDisplayName()
    {
        Assert.Equal(
            [
                nameof(OrganizationMemberLookupItemResponse.Id),
                nameof(OrganizationMemberLookupItemResponse.DisplayName)
            ],
            GetPropertyNames<OrganizationMemberLookupItemResponse>());
        Assert.Equal(
            [
                nameof(OrganizationMemberLookupResponse.Items),
                nameof(OrganizationMemberLookupResponse.PageNumber),
                nameof(OrganizationMemberLookupResponse.PageSize),
                nameof(OrganizationMemberLookupResponse.HasNext)
            ],
            GetPropertyNames<OrganizationMemberLookupResponse>());

        string[] forbiddenNames =
        [
            "UserId",
            "OrganizationId",
            "Role",
            "IsActive",
            "Email",
            "CreatedAt",
            "MembershipId",
            "Session"
        ];

        Assert.DoesNotContain(
            typeof(OrganizationMemberLookupItemResponse).GetProperties(),
            property => forbiddenNames.Contains(
                property.Name,
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Lookup_AnonymousRequest_ReturnsEmptyNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetLookupPath(Guid.NewGuid()));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task Lookup_ActiveOrganizationRole_ReturnsOwnMembershipWithoutCsrf(
        OrganizationRole role)
    {
        User caller = CreateUser($"{role} Caller");
        Organization organization = CreateOrganization($"{role} Lookup");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            role,
            1);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage response = await SendLookupAsync(
            GetLookupPath(organization.Id),
            rawHandle);
        OrganizationMemberLookupResponse? result = await response.Content
            .ReadFromJsonAsync<OrganizationMemberLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(result);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(20, result.PageSize);
        Assert.False(result.HasNext);
        OrganizationMemberLookupItemResponse item = Assert.Single(result.Items);
        Assert.NotEqual(caller.Id, membership.Id);
        Assert.Equal(membership.Id, item.Id);
        Assert.NotEqual(caller.Id, item.Id);
        Assert.Equal(caller.Name, item.DisplayName);
    }

    [Fact]
    public async Task Lookup_WithoutLiveOrganizationAccess_ReturnsEmptyNoStoreForbidden()
    {
        User caller = CreateUser("Denied Caller");
        Organization organization = CreateOrganization("Denied Lookup");
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            []);

        using HttpResponseMessage response = await SendLookupAsync(
            GetLookupPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lookup_WithActivityTenancyDualMembershipAndSearch_ReturnsMinimalMembershipProjection()
    {
        Organization organizationA = CreateOrganization("Comprehensive A");
        Organization organizationB = CreateOrganization("Comprehensive B");
        User caller = CreateUser("Alpha Caller");
        User beta = CreateUser("Beta Member");
        User special = CreateUser("Zulu Literal %_\\; TARGET");
        User inactiveMembershipUser = CreateUser("Inactive Membership");
        User inactiveUser = CreateUser("Inactive User");
        inactiveUser.Deactivate();
        User crossTenantUser = CreateUser("Cross Tenant Only");
        User dualUser = CreateUser("Dual Member");
        User duplicateA = CreateUser("Same Name");
        User duplicateB = CreateUser("Same Name");
        OrganizationMembership callerMembership = CreateMembership(
            organizationA,
            caller,
            OrganizationRole.Member,
            1);
        OrganizationMembership callerMembershipB = CreateMembership(
            organizationB,
            caller,
            OrganizationRole.Member,
            2);
        OrganizationMembership betaMembership = CreateMembership(
            organizationA,
            beta,
            OrganizationRole.Member,
            3);
        OrganizationMembership specialMembership = CreateMembership(
            organizationA,
            special,
            OrganizationRole.Member,
            4);
        OrganizationMembership inactiveMembership = CreateMembership(
            organizationA,
            inactiveMembershipUser,
            OrganizationRole.Member,
            5);
        inactiveMembership.Deactivate();
        OrganizationMembership inactiveUserMembership = CreateMembership(
            organizationA,
            inactiveUser,
            OrganizationRole.Member,
            6);
        OrganizationMembership crossTenantMembership = CreateMembership(
            organizationB,
            crossTenantUser,
            OrganizationRole.Member,
            7);
        OrganizationMembership dualMembershipA = CreateMembership(
            organizationA,
            dualUser,
            OrganizationRole.Member,
            8);
        OrganizationMembership dualMembershipB = CreateMembership(
            organizationB,
            dualUser,
            OrganizationRole.Owner,
            9);
        OrganizationMembership duplicateMembershipA = CreateMembership(
            organizationA,
            duplicateA,
            OrganizationRole.Owner,
            10);
        OrganizationMembership duplicateMembershipB = CreateMembership(
            organizationA,
            duplicateB,
            OrganizationRole.Administrator,
            11);
        User[] users =
        [
            caller,
            beta,
            special,
            inactiveMembershipUser,
            inactiveUser,
            crossTenantUser,
            dualUser,
            duplicateA,
            duplicateB
        ];
        OrganizationMembership[] memberships =
        [
            callerMembership,
            callerMembershipB,
            betaMembership,
            specialMembership,
            inactiveMembership,
            inactiveUserMembership,
            crossTenantMembership,
            dualMembershipA,
            dualMembershipB,
            duplicateMembershipA,
            duplicateMembershipB
        ];
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organizationA, organizationB],
            users,
            memberships);

        using HttpResponseMessage firstPageResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationA.Id)}?pageNumber=1&pageSize=2",
            rawHandle);
        using HttpResponseMessage secondPageResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationA.Id)}?pageNumber=2&pageSize=2",
            rawHandle);
        OrganizationMemberLookupResponse? firstPage = await firstPageResponse.Content
            .ReadFromJsonAsync<OrganizationMemberLookupResponse>();
        OrganizationMemberLookupResponse? secondPage = await secondPageResponse.Content
            .ReadFromJsonAsync<OrganizationMemberLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.True(firstPageResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasNext);
        Assert.Equal(2, secondPage.PageNumber);
        Assert.Equal(2, secondPage.PageSize);
        Assert.Contains(firstPage.Items, item => item.Id == callerMembership.Id);
        Assert.Contains(secondPage.Items, item => item.Id == dualMembershipA.Id);
        Assert.DoesNotContain(
            firstPage.Items.Concat(secondPage.Items),
            item => item.Id == inactiveMembership.Id ||
                item.Id == inactiveUserMembership.Id ||
                item.Id == crossTenantMembership.Id ||
                item.Id == dualMembershipB.Id);

        string normalizedLiteralSearch = Uri.EscapeDataString(
            "  zulu literal %_\\; target  ");
        using HttpResponseMessage searchResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationA.Id)}?search={normalizedLiteralSearch}",
            rawHandle);
        string searchJson = await searchResponse.Content.ReadAsStringAsync();
        using JsonDocument searchDocument = JsonDocument.Parse(searchJson);
        OrganizationMemberLookupResponse? searchResult = JsonSerializer.Deserialize<
            OrganizationMemberLookupResponse>(
                searchJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.NotNull(searchResult);
        OrganizationMemberLookupItemResponse searchItem = Assert.Single(
            searchResult.Items);
        Assert.Equal(specialMembership.Id, searchItem.Id);
        Assert.NotEqual(special.Id, searchItem.Id);
        Assert.Equal(special.Name, searchItem.DisplayName);
        Assert.Equal(
            ["id", "displayName"],
            searchDocument.RootElement
                .GetProperty("items")[0]
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        using HttpResponseMessage duplicateResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationA.Id)}?search=Same%20Name",
            rawHandle);
        OrganizationMemberLookupResponse? duplicateResult = await duplicateResponse
            .Content.ReadFromJsonAsync<OrganizationMemberLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.NotNull(duplicateResult);
        Assert.Equal(
            new[] { duplicateMembershipA.Id, duplicateMembershipB.Id }.OrderBy(id => id),
            duplicateResult.Items.Select(item => item.Id));

        using HttpResponseMessage crossTenantResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationA.Id)}?search=Cross%20Tenant%20Only",
            rawHandle);
        OrganizationMemberLookupResponse? emptyResult = await crossTenantResponse
            .Content.ReadFromJsonAsync<OrganizationMemberLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, crossTenantResponse.StatusCode);
        Assert.NotNull(emptyResult);
        Assert.Empty(emptyResult.Items);
        Assert.False(emptyResult.HasNext);

        using HttpResponseMessage dualBResponse = await SendLookupAsync(
            $"{GetLookupPath(organizationB.Id)}?search=Dual%20Member",
            rawHandle);
        OrganizationMemberLookupResponse? dualBResult = await dualBResponse.Content
            .ReadFromJsonAsync<OrganizationMemberLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, dualBResponse.StatusCode);
        Assert.NotNull(dualBResult);
        Assert.Equal(dualMembershipB.Id, Assert.Single(dualBResult.Items).Id);
    }

    [Fact]
    public async Task Lookup_WithInvalidInput_ReturnsNoStoreBadRequest()
    {
        User caller = CreateUser("Validation Caller");
        Organization organization = CreateOrganization("Validation Lookup");
        OrganizationMembership membership = CreateMembership(
            organization,
            caller,
            OrganizationRole.Owner,
            1);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            caller,
            [organization],
            [caller],
            [membership]);

        using HttpResponseMessage paginationResponse = await SendLookupAsync(
            $"{GetLookupPath(organization.Id)}?pageNumber=0&pageSize=101",
            rawHandle);
        using HttpResponseMessage searchResponse = await SendLookupAsync(
            $"{GetLookupPath(organization.Id)}?search={new string('x', 151)}",
            rawHandle);

        await AssertProblemResponseAsync(
            paginationResponse,
            HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(searchResponse, HttpStatusCode.BadRequest);
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

    private async Task<HttpResponseMessage> SendLookupAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static User CreateUser(string name)
    {
        return new User(
            name,
            $"{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
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
        OrganizationRole role,
        int createdMinutesLater)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1).AddMinutes(createdMinutesLater));
    }

    private static string GetLookupPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/members/lookup";
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties().Select(property => property.Name).ToArray();
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
        string responseContent = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", responseContent);
        Assert.DoesNotContain("stackTrace", responseContent);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
