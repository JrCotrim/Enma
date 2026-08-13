using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Clients;
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
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Clients;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash = "synthetic-client-endpoint-password-hash";

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

    public ClientEndpointTests(PostgreSqlFixture fixture)
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
    public void ClientContracts_CurrentScope_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [nameof(CreateClientRequest.Name)],
            GetPropertyNames<CreateClientRequest>());
        Assert.Equal(
            [nameof(UpdateClientRequest.Name)],
            GetPropertyNames<UpdateClientRequest>());
        Assert.Equal(
            [nameof(CreateClientResponse.Id)],
            GetPropertyNames<CreateClientResponse>());
        Assert.Equal(
            [
                nameof(ClientResponse.Id),
                nameof(ClientResponse.Name),
                nameof(ClientResponse.IsActive),
                nameof(ClientResponse.CreatedAt)
            ],
            GetPropertyNames<ClientResponse>());
        Assert.Equal(
            [
                nameof(ListClientsResponse.Items),
                nameof(ListClientsResponse.PageNumber),
                nameof(ListClientsResponse.PageSize)
            ],
            GetPropertyNames<ListClientsResponse>());
        Assert.Equal(
            [
                nameof(ActiveClientLookupItemResponse.Id),
                nameof(ActiveClientLookupItemResponse.Name)
            ],
            GetPropertyNames<ActiveClientLookupItemResponse>());
        Assert.Equal(
            [
                nameof(ActiveClientLookupResponse.Items),
                nameof(ActiveClientLookupResponse.PageNumber),
                nameof(ActiveClientLookupResponse.PageSize),
                nameof(ActiveClientLookupResponse.HasNext)
            ],
            GetPropertyNames<ActiveClientLookupResponse>());

        string[] forbiddenNames =
        [
            "OrganizationId",
            "TenantId",
            "UserId",
            "ClientId",
            "Role",
            "OrganizationRole",
            "MembershipRole"
        ];
        Type[] contractTypes =
        [
            typeof(CreateClientRequest),
            typeof(UpdateClientRequest),
            typeof(CreateClientResponse),
            typeof(ClientResponse),
            typeof(ListClientsResponse),
            typeof(ActiveClientLookupItemResponse),
            typeof(ActiveClientLookupResponse)
        ];

        foreach (Type contractType in contractTypes)
        {
            Assert.DoesNotContain(
                contractType.GetProperties(),
                property => forbiddenNames.Contains(
                    property.Name,
                    StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task ClientEndpoints_AnonymousRequests_ReturnEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        string path = GetClientPath(Guid.NewGuid(), Guid.NewGuid());

        using HttpResponseMessage getResponse = await client.GetAsync(path);
        using HttpResponseMessage lookupResponse = await client.GetAsync(
            GetClientLookupPath(Guid.NewGuid()));
        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            GetClientsPath(Guid.NewGuid()),
            new { name = "Anonymous Client" });

        await AssertEmptyResponseAsync(
            getResponse,
            HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(
            lookupResponse,
            HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(
            createResponse,
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ClientEndpoints_MissingOrganizationAccess_ReturnEmptyNoStoreForbiddenBeforeCsrf()
    {
        User user = CreateUser("organization-denied");
        Organization organization = CreateOrganization("Denied");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [],
            []);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetClientsPath(organization.Id),
            rawHandle);
        using HttpResponseMessage lookupResponse = await SendGetAsync(
            GetClientLookupPath(organization.Id),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf: null,
            new { name = "Denied Client" });

        await AssertEmptyResponseAsync(
            listResponse,
            HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            lookupResponse,
            HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            createResponse,
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAndListClients_MemberInOrganization_ReturnCurrentTenantDataWithoutAuthorizationFields()
    {
        User user = CreateUser("member-read");
        Organization organization = CreateOrganization("Member Read");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity activeClient = CreateClient(organization, "Active Client", 2);
        ClientEntity inactiveClient = CreateClient(
            organization,
            "Inactive Client",
            1);
        inactiveClient.Deactivate();
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [activeClient, inactiveClient]);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetClientPath(organization.Id, activeClient.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        string getJson = await getResponse.Content.ReadAsStringAsync();
        using JsonDocument getDocument = JsonDocument.Parse(getJson);
        Assert.Equal(
            ["id", "name", "isActive", "createdAt"],
            getDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        ClientResponse? getResult = JsonSerializer.Deserialize<ClientResponse>(
            getJson,
            JsonSerializerOptions.Web);
        Assert.NotNull(getResult);
        Assert.Equal(activeClient.Id, getResult.Id);
        Assert.Equal(activeClient.Name, getResult.Name);
        Assert.True(getResult.IsActive);
        Assert.Equal(activeClient.CreatedAt, getResult.CreatedAt);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetClientsPath(organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        ListClientsResponse? listResult =
            await listResponse.Content.ReadFromJsonAsync<ListClientsResponse>();
        Assert.NotNull(listResult);
        Assert.Equal(1, listResult.PageNumber);
        Assert.Equal(20, listResult.PageSize);
        Assert.Equal(2, listResult.Items.Count);
        Assert.Contains(
            listResult.Items,
            item => item.Id == activeClient.Id && item.IsActive);
        Assert.Contains(
            listResult.Items,
            item => item.Id == inactiveClient.Id && !item.IsActive);
    }

    [Fact]
    public async Task ClientMutations_MemberWithValidCsrf_ReturnForbiddenWithoutMutation()
    {
        User user = CreateUser("member-mutations");
        Organization organization = CreateOrganization("Member Mutations");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity activeClient = CreateClient(organization, "Original Active", 2);
        ClientEntity inactiveClient = CreateClient(organization, "Original Inactive", 1);
        inactiveClient.Deactivate();
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [activeClient, inactiveClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "Denied Create" });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, activeClient.Id),
            rawHandle,
            csrf,
            new { name = "Denied Update" });
        using HttpResponseMessage deactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, activeClient.Id)}/deactivate",
            rawHandle,
            csrf);
        using HttpResponseMessage reactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, inactiveClient.Id)}/reactivate",
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(deactivateResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(reactivateResponse, HttpStatusCode.Forbidden);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientEntity[] persistedClients = await dbContext.Clients
            .AsNoTracking()
            .OrderBy(candidate => candidate.Name)
            .ToArrayAsync();
        Assert.Equal(2, persistedClients.Length);
        Assert.Contains(
            persistedClients,
            candidate => candidate.Id == activeClient.Id &&
                candidate.Name == "Original Active" &&
                candidate.IsActive);
        Assert.Contains(
            persistedClients,
            candidate => candidate.Id == inactiveClient.Id &&
                candidate.Name == "Original Inactive" &&
                !candidate.IsActive);
    }

    [Fact]
    public async Task ClientMutations_OwnerWithValidCsrf_AllowAllCurrentActionsAndReturnContextualLocation()
    {
        User user = CreateUser("owner-mutations");
        Organization organization = CreateOrganization("Owner Mutations");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "  Created Client  " });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);
        CreateClientResponse? created =
            await createResponse.Content.ReadFromJsonAsync<CreateClientResponse>();
        Assert.NotNull(created);
        Assert.Equal(
            GetClientPath(organization.Id, created.Id),
            createResponse.Headers.Location?.OriginalString);
        string createJson = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createDocument = JsonDocument.Parse(createJson);
        Assert.Equal(
            ["id"],
            createDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, created.Id),
            rawHandle,
            csrf,
            new { name = "Updated Client" });
        using HttpResponseMessage deactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/deactivate",
            rawHandle,
            csrf);
        using HttpResponseMessage deactivateAgainResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/deactivate",
            rawHandle,
            csrf);
        using HttpResponseMessage reactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/reactivate",
            rawHandle,
            csrf);
        using HttpResponseMessage reactivateAgainResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/reactivate",
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(deactivateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(deactivateAgainResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(reactivateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(reactivateAgainResponse, HttpStatusCode.NoContent);

        ClientEntity persisted = await GetPersistedClientAsync(created.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal("Updated Client", persisted.Name);
        Assert.True(persisted.IsActive);
        Assert.Equal(Now, persisted.CreatedAt);
    }

    [Fact]
    public async Task ClientMutations_AdministratorWithValidCsrf_AllowAllCurrentActions()
    {
        User user = CreateUser("administrator-mutations");
        Organization organization = CreateOrganization("Administrator Mutations");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Administrator);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "Administrator Client" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        CreateClientResponse? created =
            await createResponse.Content.ReadFromJsonAsync<CreateClientResponse>();
        Assert.NotNull(created);

        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, created.Id),
            rawHandle,
            csrf,
            new { name = "Administrator Updated" });
        using HttpResponseMessage deactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/deactivate",
            rawHandle,
            csrf);
        using HttpResponseMessage reactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, created.Id)}/reactivate",
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(deactivateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(reactivateResponse, HttpStatusCode.NoContent);
        ClientEntity persisted = await GetPersistedClientAsync(created.Id);
        Assert.Equal("Administrator Updated", persisted.Name);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task CreateClient_ContextualRolesDiffer_UsesLiveRoleForRouteOrganization()
    {
        User user = CreateUser("contextual-role");
        Organization organizationA = CreateOrganization("Context A");
        Organization organizationB = CreateOrganization("Context B");
        OrganizationMembership memberA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership ownerB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [memberA, ownerB],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage responseA = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organizationA.Id),
            rawHandle,
            csrf,
            new { name = "Context A Client" });
        using HttpResponseMessage responseB = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organizationB.Id),
            rawHandle,
            csrf,
            new { name = "Context B Client" });

        await AssertEmptyResponseAsync(responseA, HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientEntity persisted = await dbContext.Clients
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(organizationB.Id, persisted.OrganizationId);
    }

    [Fact]
    public async Task CreateClient_RoleChangesWithoutRelogin_UsesUpdatedLiveRole()
    {
        User user = CreateUser("live-role");
        Organization organization = CreateOrganization("Live Role");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage deniedResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "Before Role Change" });
        await AssertEmptyResponseAsync(deniedResponse, HttpStatusCode.Forbidden);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await dbContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membership.Id);
            persistedMembership.ChangeRole(OrganizationRole.Administrator);
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage allowedResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "After Role Change" });

        Assert.Equal(HttpStatusCode.Created, allowedResponse.StatusCode);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        ClientEntity persistedClient = await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal("After Role Change", persistedClient.Name);
    }

    [Fact]
    public async Task GetClient_MissingOrCrossTenantClient_ReturnsSameEmptyNoStoreNotFound()
    {
        User user = CreateUser("cross-tenant-get");
        Organization organizationA = CreateOrganization("Get A");
        Organization organizationB = CreateOrganization("Get B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        ClientEntity clientB = CreateClient(organizationB, "Client B", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA],
            [clientB]);

        using HttpResponseMessage crossTenantResponse = await SendGetAsync(
            GetClientPath(organizationA.Id, clientB.Id),
            rawHandle);
        using HttpResponseMessage missingResponse = await SendGetAsync(
            GetClientPath(organizationA.Id, Guid.NewGuid()),
            rawHandle);

        await AssertEmptyResponseAsync(
            crossTenantResponse,
            HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetClient_DualMembership_RequiresMatchingRouteOrganizationAndClientOwnership()
    {
        User user = CreateUser("dual-membership-get");
        Organization organizationA = CreateOrganization("Dual Get A");
        Organization organizationB = CreateOrganization("Dual Get B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Member);
        ClientEntity clientB = CreateClient(organizationB, "Dual Client B", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientB]);

        using HttpResponseMessage responseA = await SendGetAsync(
            GetClientPath(organizationA.Id, clientB.Id),
            rawHandle);
        using HttpResponseMessage responseB = await SendGetAsync(
            GetClientPath(organizationB.Id, clientB.Id),
            rawHandle);

        await AssertEmptyResponseAsync(responseA, HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
        ClientResponse? result =
            await responseB.Content.ReadFromJsonAsync<ClientResponse>();
        Assert.NotNull(result);
        Assert.Equal(clientB.Id, result.Id);
        Assert.Equal("Dual Client B", result.Name);
    }

    [Fact]
    public async Task UpdateClient_DualOwnerMembership_CannotCrossTenantBoundary()
    {
        User user = CreateUser("cross-tenant-update");
        Organization organizationA = CreateOrganization("Update A");
        Organization organizationB = CreateOrganization("Update B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientB = CreateClient(organizationB, "Original B", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage crossTenantResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organizationA.Id, clientB.Id),
            rawHandle,
            csrf,
            new { name = "Cross Tenant Name" });

        await AssertEmptyResponseAsync(
            crossTenantResponse,
            HttpStatusCode.NotFound);
        Assert.Equal("Original B", (await GetPersistedClientAsync(clientB.Id)).Name);

        using HttpResponseMessage ownTenantResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organizationB.Id, clientB.Id),
            rawHandle,
            csrf,
            new { name = "Own Tenant Name" });

        await AssertEmptyResponseAsync(
            ownTenantResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(
            "Own Tenant Name",
            (await GetPersistedClientAsync(clientB.Id)).Name);
    }

    [Fact]
    public async Task DeactivateClient_DualOwnerMembership_CannotCrossTenantBoundary()
    {
        User user = CreateUser("cross-tenant-lifecycle");
        Organization organizationA = CreateOrganization("Lifecycle A");
        Organization organizationB = CreateOrganization("Lifecycle B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientB = CreateClient(organizationB, "Lifecycle B Client", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage crossTenantResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organizationA.Id, clientB.Id)}/deactivate",
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(
            crossTenantResponse,
            HttpStatusCode.NotFound);
        Assert.True((await GetPersistedClientAsync(clientB.Id)).IsActive);

        using HttpResponseMessage ownTenantResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organizationB.Id, clientB.Id)}/deactivate",
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(
            ownTenantResponse,
            HttpStatusCode.NoContent);
        Assert.False((await GetPersistedClientAsync(clientB.Id)).IsActive);
    }

    [Fact]
    public async Task ListClients_DualMembershipAndPagination_ReturnsOnlyContextualTenantIncludingInactive()
    {
        User user = CreateUser("list-isolation");
        Organization organizationA = CreateOrganization("List A");
        Organization organizationB = CreateOrganization("List B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Member);
        ClientEntity clientA1 = CreateClient(organizationA, "A Alpha", 4);
        ClientEntity clientA2 = CreateClient(organizationA, "A Zeta", 3);
        clientA2.Deactivate();
        ClientEntity clientB1 = CreateClient(organizationB, "B Alpha", 2);
        ClientEntity clientB2 = CreateClient(organizationB, "B Zeta", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientA1, clientA2, clientB1, clientB2]);

        using HttpResponseMessage firstPageResponse = await SendGetAsync(
            $"{GetClientsPath(organizationA.Id)}?pageNumber=1&pageSize=1",
            rawHandle);
        using HttpResponseMessage secondPageResponse = await SendGetAsync(
            $"{GetClientsPath(organizationA.Id)}?pageNumber=2&pageSize=1",
            rawHandle);
        ListClientsResponse? firstPage =
            await firstPageResponse.Content.ReadFromJsonAsync<ListClientsResponse>();
        ListClientsResponse? secondPage =
            await secondPageResponse.Content.ReadFromJsonAsync<ListClientsResponse>();

        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.True(firstPageResponse.Headers.CacheControl?.NoStore);
        Assert.True(secondPageResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        ClientResponse firstItem = Assert.Single(firstPage.Items);
        ClientResponse secondItem = Assert.Single(secondPage.Items);
        Assert.Equal(clientA1.Id, firstItem.Id);
        Assert.True(firstItem.IsActive);
        Assert.Equal(clientA2.Id, secondItem.Id);
        Assert.False(secondItem.IsActive);
        Assert.DoesNotContain(
            new[] { firstItem.Id, secondItem.Id },
            id => id == clientB1.Id || id == clientB2.Id);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task LookupActiveClients_WithClientViewRole_ReturnsActiveClients(
        OrganizationRole role)
    {
        User user = CreateUser($"lookup-{role}");
        Organization organization = CreateOrganization($"Lookup {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);
        ClientEntity activeClient = CreateClient(
            organization,
            $"{role} Active Client",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [activeClient]);

        using HttpResponseMessage response = await SendGetAsync(
            GetClientLookupPath(organization.Id),
            rawHandle);
        ActiveClientLookupResponse? result =
            await response.Content.ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(result);
        ActiveClientLookupItemResponse item = Assert.Single(result.Items);
        Assert.Equal(activeClient.Id, item.Id);
        Assert.Equal(activeClient.Name, item.Name);
    }

    [Fact]
    public async Task LookupActiveClients_WithDualMembershipPaginationAndSearch_IsActiveTenantBoundAndDiscoverable()
    {
        User user = CreateUser("lookup-discoverability");
        Organization organizationA = CreateOrganization("Lookup A");
        Organization organizationB = CreateOrganization("Lookup B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Member);
        ClientEntity[] pagedClients = Enumerable.Range(1, 22)
            .Select(index => CreateClient(
                organizationA,
                $"Active Client {index:D2}",
                30 - index))
            .ToArray();
        ClientEntity specialClient = CreateClient(
            organizationA,
            "Zulu Literal %_\\ TARGET",
            2);
        ClientEntity inactiveClient = CreateClient(
            organizationA,
            "Inactive Lookup Client",
            1);
        inactiveClient.Deactivate();
        ClientEntity crossTenantClient = CreateClient(
            organizationB,
            specialClient.Name,
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            pagedClients
                .Append(specialClient)
                .Append(inactiveClient)
                .Append(crossTenantClient)
                .ToArray());

        using HttpResponseMessage firstPageResponse = await SendGetAsync(
            GetClientLookupPath(organizationA.Id),
            rawHandle);
        using HttpResponseMessage secondPageResponse = await SendGetAsync(
            $"{GetClientLookupPath(organizationA.Id)}?pageNumber=2&pageSize=20",
            rawHandle);
        ActiveClientLookupResponse? firstPage =
            await firstPageResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();
        ActiveClientLookupResponse? secondPage =
            await secondPageResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.True(firstPageResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(1, firstPage.PageNumber);
        Assert.Equal(20, firstPage.PageSize);
        Assert.Equal(20, firstPage.Items.Count);
        Assert.True(firstPage.HasNext);
        Assert.Equal(2, secondPage.PageNumber);
        Assert.Equal(20, secondPage.PageSize);
        Assert.Equal(3, secondPage.Items.Count);
        Assert.False(secondPage.HasNext);
        Assert.Contains(secondPage.Items, item => item.Id == specialClient.Id);
        Assert.DoesNotContain(
            firstPage.Items.Concat(secondPage.Items),
            item => item.Id == inactiveClient.Id ||
                item.Id == crossTenantClient.Id);

        string caseInsensitiveSearch = Uri.EscapeDataString(
            "  zulu literal %_\\ target  ");
        using HttpResponseMessage searchResponse = await SendGetAsync(
            $"{GetClientLookupPath(organizationA.Id)}?search={caseInsensitiveSearch}",
            rawHandle);
        ActiveClientLookupResponse? searchResult =
            await searchResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.NotNull(searchResult);
        Assert.Equal(specialClient.Id, Assert.Single(searchResult.Items).Id);

        using HttpResponseMessage wildcardSearchResponse = await SendGetAsync(
            $"{GetClientLookupPath(organizationA.Id)}?search={Uri.EscapeDataString("%_\\")}",
            rawHandle);
        ActiveClientLookupResponse? wildcardSearchResult =
            await wildcardSearchResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, wildcardSearchResponse.StatusCode);
        Assert.NotNull(wildcardSearchResult);
        Assert.Equal(
            specialClient.Id,
            Assert.Single(wildcardSearchResult.Items).Id);

        using HttpResponseMessage crossTenantSearchResponse = await SendGetAsync(
            $"{GetClientLookupPath(organizationA.Id)}?search={Uri.EscapeDataString(crossTenantClient.Name)}",
            rawHandle);
        ActiveClientLookupResponse? crossTenantSearchResult =
            await crossTenantSearchResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, crossTenantSearchResponse.StatusCode);
        Assert.NotNull(crossTenantSearchResult);
        Assert.Equal(specialClient.Id, Assert.Single(crossTenantSearchResult.Items).Id);
        Assert.DoesNotContain(
            crossTenantSearchResult.Items,
            item => item.Id == crossTenantClient.Id);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            ClientEntity persistedSpecialClient = await dbContext.Clients
                .SingleAsync(candidate => candidate.Id == specialClient.Id);
            persistedSpecialClient.Deactivate();
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage afterDeactivationResponse = await SendGetAsync(
            $"{GetClientLookupPath(organizationA.Id)}?search={caseInsensitiveSearch}",
            rawHandle);
        ActiveClientLookupResponse? afterDeactivationResult =
            await afterDeactivationResponse.Content
                .ReadFromJsonAsync<ActiveClientLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, afterDeactivationResponse.StatusCode);
        Assert.NotNull(afterDeactivationResult);
        Assert.Empty(afterDeactivationResult.Items);
    }

    [Fact]
    public async Task LookupActiveClients_WithLiveMembershipRevocation_DeniesWithoutRelogin()
    {
        User user = CreateUser("lookup-live-membership");
        Organization organization = CreateOrganization("Lookup Live Membership");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);

        using HttpResponseMessage allowedResponse = await SendGetAsync(
            GetClientLookupPath(organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await dbContext.OrganizationMemberships.SingleAsync();
            persistedMembership.Deactivate();
            await dbContext.SaveChangesAsync();
        }

        using HttpResponseMessage deniedResponse = await SendGetAsync(
            GetClientLookupPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(
            deniedResponse,
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ClientMutations_MissingOrInvalidCsrf_ReturnBadRequestBeforeAnyMutation()
    {
        User user = CreateUser("csrf-rejection");
        Organization organization = CreateOrganization("Csrf Rejection");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity existingClient = CreateClient(organization, "Original Client", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [existingClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf: null,
            new { name = "Missing Csrf Create" });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, existingClient.Id),
            rawHandle,
            csrf,
            new { name = "Invalid Csrf Update" },
            requestTokenOverride: "malformed");
        using HttpResponseMessage deactivateResponse = await SendMutationAsync(
            HttpMethod.Post,
            $"{GetClientPath(organization.Id, existingClient.Id)}/deactivate",
            rawHandle,
            csrf: null);

        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(
            deactivateResponse,
            HttpStatusCode.BadRequest);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientEntity persisted = await dbContext.Clients
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(existingClient.Id, persisted.Id);
        Assert.Equal("Original Client", persisted.Name);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task ClientRequests_InvalidApplicationInput_ReturnControlledNoStoreBadRequestWithoutMutation()
    {
        User user = CreateUser("validation");
        Organization organization = CreateOrganization("Validation");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity existingClient = CreateClient(organization, "Valid Client", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [existingClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new { name = "   " });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, existingClient.Id),
            rawHandle,
            csrf,
            new { name = new string('x', 151) });
        using HttpResponseMessage listResponse = await SendGetAsync(
            $"{GetClientsPath(organization.Id)}?pageNumber=0&pageSize=101",
            rawHandle);
        using HttpResponseMessage lookupPaginationResponse = await SendGetAsync(
            $"{GetClientLookupPath(organization.Id)}?pageNumber=1&pageSize=101",
            rawHandle);
        using HttpResponseMessage lookupSearchResponse = await SendGetAsync(
            $"{GetClientLookupPath(organization.Id)}?search={new string('x', 151)}",
            rawHandle);

        await AssertProblemResponseAsync(createResponse, HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(updateResponse, HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(listResponse, HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(
            lookupPaginationResponse,
            HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(
            lookupSearchResponse,
            HttpStatusCode.BadRequest);
        ClientEntity persisted = await GetPersistedClientAsync(existingClient.Id);
        Assert.Equal("Valid Client", persisted.Name);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Clients.CountAsync());
    }

    [Fact]
    public async Task CreateClient_MalformedJson_ReturnsSafeBadRequestWithoutMutation()
    {
        User user = CreateUser("malformed-json");
        Organization organization = CreateOrganization("Malformed Json");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetClientsPath(organization.Id));
        AddCookiesAndCsrf(request, rawHandle, csrf, csrf.RequestToken);
        request.Content = new StringContent(
            "{",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string responseContent = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.Text.Json", responseContent);
        Assert.DoesNotContain("JsonException", responseContent);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.Clients.CountAsync());
    }

    [Fact]
    public async Task ClientRoutes_MalformedIdentifiers_DoNotMatchProductionEndpoints()
    {
        User user = CreateUser("malformed-routes");
        Organization organization = CreateOrganization("Malformed Routes");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            []);

        using HttpResponseMessage organizationResponse = await SendGetAsync(
            "/api/organizations/not-a-guid/clients",
            rawHandle);
        using HttpResponseMessage clientResponse = await SendGetAsync(
            $"{GetClientsPath(organization.Id)}/not-a-guid",
            rawHandle);

        Assert.Equal(HttpStatusCode.NotFound, organizationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, clientResponse.StatusCode);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Client HTTP {marker}",
            $"client-http-{marker}-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        return user;
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
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

    private static ClientEntity CreateClient(
        Organization organization,
        string name,
        int createdMinutesAgo)
    {
        return new ClientEntity(
            organization.Id,
            name,
            Now.AddMinutes(-createdMinutesAgo));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<ClientEntity> clients)
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
        dbContext.Clients.AddRange(clients);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse? result =
            await response.Content.ReadFromJsonAsync<CsrfResponse>();
        Assert.NotNull(result);
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));

        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair? csrf,
        object? body = null,
        string? requestTokenOverride = null)
    {
        using var request = new HttpRequestMessage(method, path);
        string? requestToken = requestTokenOverride ?? csrf?.RequestToken;
        AddCookiesAndCsrf(request, rawHandle, csrf, requestToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static void AddCookiesAndCsrf(
        HttpRequestMessage request,
        string rawHandle,
        CsrfPair? csrf,
        string? requestToken)
    {
        var cookies = new List<string>
        {
            $"{SessionCookieName}={rawHandle}"
        };

        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));

        if (requestToken is not null)
        {
            request.Headers.Add(CsrfHeaderName, requestToken);
        }
    }

    private static IReadOnlyList<SetCookieHeaderValue> ParseSetCookies(
        HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                HeaderNames.SetCookie,
                out IEnumerable<string>? values))
        {
            return [];
        }

        return SetCookieHeaderValue.ParseList(values.ToList()).ToArray();
    }

    private async Task<ClientEntity> GetPersistedClientAsync(Guid clientId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Clients
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == clientId);
    }

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
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
        Assert.DoesNotContain("exceptionType", responseContent);
    }

    private static string GetClientsPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/clients";
    }

    private static string GetClientPath(Guid organizationId, Guid clientId)
    {
        return $"{GetClientsPath(organizationId)}/{clientId:D}";
    }

    private static string GetClientLookupPath(Guid organizationId)
    {
        return $"{GetClientsPath(organizationId)}/lookup";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);
}
