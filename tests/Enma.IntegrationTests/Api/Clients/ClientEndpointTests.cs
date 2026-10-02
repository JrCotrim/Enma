using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Clients;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Clients;
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
            [
                nameof(CreateClientRequest.Name),
                nameof(CreateClientRequest.Email),
                nameof(CreateClientRequest.Phone),
                nameof(CreateClientRequest.Cpf),
                nameof(CreateClientRequest.PersonType),
                nameof(CreateClientRequest.Cnpj),
                nameof(CreateClientRequest.Address),
                nameof(CreateClientRequest.Notes)
            ],
            GetPropertyNames<CreateClientRequest>());
        Assert.Equal(
            [
                nameof(UpdateClientRequest.Name),
                nameof(UpdateClientRequest.Email),
                nameof(UpdateClientRequest.Phone),
                nameof(UpdateClientRequest.Cpf),
                nameof(UpdateClientRequest.PersonType),
                nameof(UpdateClientRequest.Cnpj),
                nameof(UpdateClientRequest.Address),
                nameof(UpdateClientRequest.Notes)
            ],
            GetPropertyNames<UpdateClientRequest>());
        Assert.Equal(
            [nameof(CreateClientResponse.Id)],
            GetPropertyNames<CreateClientResponse>());
        Assert.Equal(
            [
                nameof(ClientResponse.Id),
                nameof(ClientResponse.Name),
                nameof(ClientResponse.Email),
                nameof(ClientResponse.Phone),
                nameof(ClientResponse.Cpf),
                nameof(ClientResponse.IsActive),
                nameof(ClientResponse.CreatedAt),
                nameof(ClientResponse.PersonType),
                nameof(ClientResponse.Cnpj),
                nameof(ClientResponse.Address),
                nameof(ClientResponse.Notes)
            ],
            GetPropertyNames<ClientResponse>());
        Assert.Equal(
            [
                nameof(ClientSummaryResponse.Id),
                nameof(ClientSummaryResponse.Name),
                nameof(ClientSummaryResponse.IsActive),
                nameof(ClientSummaryResponse.CreatedAt)
            ],
            GetPropertyNames<ClientSummaryResponse>());
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
            typeof(ClientSummaryResponse),
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
        ClientEntity activeClient = CreateClient(
            organization,
            "Active Client",
            2,
            "list-pii@example.test",
            "11987654321",
            "52998224725");
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
            [
                "id",
                "name",
                "email",
                "phone",
                "cpf",
                "isActive",
                "createdAt",
                "personType",
                "cnpj",
                "address",
                "notes"
            ],
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
        Assert.Equal(activeClient.Email, getResult.Email);
        Assert.Equal(activeClient.Phone, getResult.Phone);
        Assert.Equal(activeClient.Cpf, getResult.Cpf);
        Assert.True(getResult.IsActive);
        Assert.Equal(activeClient.CreatedAt, getResult.CreatedAt);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetClientsPath(organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        using JsonDocument listDocument = JsonDocument.Parse(listJson);
        JsonElement[] listItems = listDocument.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.All(listItems, item => Assert.Equal(
            ["id", "name", "isActive", "createdAt"],
            item.EnumerateObject().Select(property => property.Name).ToArray()));
        Assert.DoesNotContain("email", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cpf", listJson, StringComparison.OrdinalIgnoreCase);
        ListClientsResponse? listResult = JsonSerializer.Deserialize<ListClientsResponse>(
            listJson,
            JsonSerializerOptions.Web);
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
            UpdateBody("Denied Update"));
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
            UpdateBody("Updated Client"));
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
            UpdateBody("Administrator Updated"));
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

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ClientProfile_AdministrativeRoles_CreateUpdateNormalizeAndClearOptionalFields(
        OrganizationRole role)
    {
        User user = CreateUser($"profile-{role}");
        Organization organization = CreateOrganization($"Profile {role}");
        OrganizationMembership membership = CreateMembership(user, organization, role);
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
            new
            {
                name = "  Profile Client  ",
                email = "  PROFILE@Example.TEST  ",
                phone = " +55 (11) 98765-4321 ",
                cpf = "529.982.247-25"
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        CreateClientResponse? created =
            await createResponse.Content.ReadFromJsonAsync<CreateClientResponse>();
        Assert.NotNull(created);

        using HttpResponseMessage createdDetailResponse = await SendGetAsync(
            GetClientPath(organization.Id, created.Id),
            rawHandle);
        ClientResponse? createdDetail = await createdDetailResponse.Content
            .ReadFromJsonAsync<ClientResponse>();

        Assert.Equal(HttpStatusCode.OK, createdDetailResponse.StatusCode);
        Assert.NotNull(createdDetail);
        Assert.Equal("Profile Client", createdDetail.Name);
        Assert.Equal("profile@example.test", createdDetail.Email);
        Assert.Equal("5511987654321", createdDetail.Phone);
        Assert.Equal("52998224725", createdDetail.Cpf);

        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, created.Id),
            rawHandle,
            csrf,
            UpdateBody(
                "  Updated Profile  ",
                "  UPDATED@Example.TEST ",
                "(21) 2345-6789",
                "111.444.777-35"));

        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
        ClientEntity updated = await GetPersistedClientAsync(created.Id);
        Assert.Equal("Updated Profile", updated.Name);
        Assert.Equal("updated@example.test", updated.Email);
        Assert.Equal("2123456789", updated.Phone);
        Assert.Equal("11144477735", updated.Cpf);

        using HttpResponseMessage clearResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, created.Id),
            rawHandle,
            csrf,
            UpdateBody("Updated Profile", "  ", " ", ""));
        using HttpResponseMessage clearedDetailResponse = await SendGetAsync(
            GetClientPath(organization.Id, created.Id),
            rawHandle);
        ClientResponse? clearedDetail = await clearedDetailResponse.Content
            .ReadFromJsonAsync<ClientResponse>();

        await AssertEmptyResponseAsync(clearResponse, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, clearedDetailResponse.StatusCode);
        Assert.NotNull(clearedDetail);
        Assert.Null(clearedDetail.Email);
        Assert.Null(clearedDetail.Phone);
        Assert.Null(clearedDetail.Cpf);
    }

    [Theory]
    [InlineData("not-an-email", "(11) 98765-4321", "529.982.247-25")]
    [InlineData("changed@example.test", "123", "529.982.247-25")]
    [InlineData("changed@example.test", "(11) 98765-4321", "111.111.111-11")]
    public async Task UpdateClient_InvalidProfile_ReturnsBadRequestWithoutPartialMutation(
        string email,
        string phone,
        string cpf)
    {
        User user = CreateUser("invalid-profile");
        Organization organization = CreateOrganization("Invalid Profile");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        var existingClient = new ClientEntity(
            organization.Id,
            "Original Profile",
            Now.AddMinutes(-1),
            "original@example.test",
            "11987654321",
            "52998224725");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [existingClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, existingClient.Id),
            rawHandle,
            csrf,
            UpdateBody("Partially Changed", email, phone, cpf));

        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        ClientEntity persisted = await GetPersistedClientAsync(existingClient.Id);
        Assert.Equal("Original Profile", persisted.Name);
        Assert.Equal("original@example.test", persisted.Email);
        Assert.Equal("11987654321", persisted.Phone);
        Assert.Equal("52998224725", persisted.Cpf);
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
            UpdateBody("Cross Tenant Name"));

        await AssertEmptyResponseAsync(
            crossTenantResponse,
            HttpStatusCode.NotFound);
        Assert.Equal("Original B", (await GetPersistedClientAsync(clientB.Id)).Name);

        using HttpResponseMessage ownTenantResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organizationB.Id, clientB.Id),
            rawHandle,
            csrf,
            UpdateBody("Own Tenant Name"));

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
        ClientSummaryResponse firstItem = Assert.Single(firstPage.Items);
        ClientSummaryResponse secondItem = Assert.Single(secondPage.Items);
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
            UpdateBody("Invalid Csrf Update"),
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
            UpdateBody(new string('x', 151)));
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

    [Fact]
    public async Task CreateClient_IndividualAndCompanyProfiles_ReturnCreatedAndDetailExposesNewFields()
    {
        User user = CreateUser("person-type-create");
        Organization organization = CreateOrganization("Person Type Create");
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

        (object Body, string PersonType, string? Cpf, string? Cnpj, string? Address, string? Notes)[] cases =
        [
            (new { name = "PF Sem Documento" }, "individual", null, null, null, null),
            (
                new { name = "PF Com CPF", cpf = "529.982.247-25", address = " Rua A, 1 ", notes = " Nota PF " },
                "individual",
                "52998224725",
                null,
                "Rua A, 1",
                "Nota PF"),
            (
                new { name = "PJ Numerica", personType = "company", cnpj = "11.222.333/0001-81" },
                "company",
                null,
                "11222333000181",
                null,
                null),
            (
                new { name = "PJ Alfanumerica", personType = "company", cnpj = "12.abc.345/01de-35", notes = "Nota PJ" },
                "company",
                null,
                "12ABC34501DE35",
                null,
                "Nota PJ")
        ];

        foreach ((object body, string personType, string? cpf, string? cnpj, string? address, string? notes) in cases)
        {
            using HttpResponseMessage createResponse = await SendMutationAsync(
                HttpMethod.Post,
                GetClientsPath(organization.Id),
                rawHandle,
                csrf,
                body);

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            CreateClientResponse? created =
                await createResponse.Content.ReadFromJsonAsync<CreateClientResponse>();
            Assert.NotNull(created);

            using HttpResponseMessage detailResponse = await SendGetAsync(
                GetClientPath(organization.Id, created.Id),
                rawHandle);
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using JsonDocument detail = JsonDocument.Parse(
                await detailResponse.Content.ReadAsStringAsync());
            JsonElement root = detail.RootElement;
            Assert.Equal(personType, root.GetProperty("personType").GetString());
            Assert.Equal(cpf, root.GetProperty("cpf").GetString());
            Assert.Equal(cnpj, root.GetProperty("cnpj").GetString());
            Assert.Equal(address, root.GetProperty("address").GetString());
            Assert.Equal(notes, root.GetProperty("notes").GetString());
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(4, await dbContext.Clients.CountAsync());
    }

    [Theory]
    [InlineData("company", null, "12.345.678/0001-00", "cnpj")]
    [InlineData("individual", null, "11.222.333/0001-81", "cnpj")]
    [InlineData(null, null, "12.ABC.345/01DE-35", "cnpj")]
    [InlineData("company", "529.982.247-25", null, "cpf")]
    [InlineData("individual", "111.111.111-11", null, "cpf")]
    [InlineData("Company", null, null, "person type")]
    [InlineData("pj", null, null, "person type")]
    public async Task CreateClient_InvalidOrMismatchedDocument_ReturnsBadRequestWithoutEchoingValue(
        string? personType,
        string? cpf,
        string? cnpj,
        string expectedField)
    {
        User user = CreateUser("invalid-document");
        Organization organization = CreateOrganization("Invalid Document");
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

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                name = "Invalid Document Client",
                personType,
                cpf,
                cnpj,
                address = "Synthetic address",
                notes = "Synthetic notes"
            });

        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedField, content, StringComparison.Ordinal);
        AssertDoesNotContainDocument(content, cpf);
        AssertDoesNotContainDocument(content, cnpj);
        Assert.DoesNotContain("Synthetic address", content);
        Assert.DoesNotContain("Synthetic notes", content);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateClient_DocumentUsedInSameOrganization_ReturnsFixedConflictAndOtherOrganizationAccepts(
        bool company,
        bool existingInactive)
    {
        User user = CreateUser("duplicate-document");
        Organization organizationA = CreateOrganization("Duplicate A");
        Organization organizationB = CreateOrganization("Duplicate B");
        OrganizationMembership ownerA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership ownerB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity existing = company
            ? new ClientEntity(
                organizationA.Id,
                "Existing Company",
                Now.AddMinutes(-5),
                personType: PersonType.Company,
                cnpj: "12ABC34501DE35")
            : new ClientEntity(
                organizationA.Id,
                "Existing Individual",
                Now.AddMinutes(-5),
                cpf: "52998224725");

        if (existingInactive)
        {
            existing.Deactivate();
        }

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [ownerA, ownerB],
            [existing]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        object body = company
            ? new { name = "Duplicate Company", personType = "company", cnpj = "12.abc.345/01de-35" }
            : new { name = "Duplicate Individual", cpf = "529.982.247-25" };

        using HttpResponseMessage conflictResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organizationA.Id),
            rawHandle,
            csrf,
            body);
        using HttpResponseMessage otherOrganizationResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetClientsPath(organizationB.Id),
            rawHandle,
            csrf,
            body);

        await AssertDuplicateDocumentConflictAsync(conflictResponse);
        Assert.Equal(HttpStatusCode.Created, otherOrganizationResponse.StatusCode);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            1,
            await dbContext.Clients.CountAsync(
                candidate => candidate.OrganizationId == organizationA.Id));
        Assert.Equal(
            1,
            await dbContext.Clients.CountAsync(
                candidate => candidate.OrganizationId == organizationB.Id));
    }

    [Fact]
    public async Task CreateClient_ConcurrentSameDocument_CreatesOneAndRejectsOther()
    {
        User user = CreateUser("concurrent-document");
        Organization organization = CreateOrganization("Concurrent Document");
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

        Task<HttpResponseMessage>[] requests = Enumerable.Range(1, 2)
            .Select(index => SendMutationAsync(
                HttpMethod.Post,
                GetClientsPath(organization.Id),
                rawHandle,
                csrf,
                new
                {
                    name = $"Concurrent Company {index}",
                    personType = "company",
                    cnpj = index == 1 ? "11.222.333/0001-81" : "11222333000181"
                }))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        try
        {
            Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Created);
            HttpResponseMessage conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertDuplicateDocumentConflictAsync(conflict);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(1, await dbContext.Clients.CountAsync());
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateClient_CopyingOtherDocumentConflictsAndKeepingOwnDocumentSucceeds(
        bool company)
    {
        User user = CreateUser("update-document");
        Organization organization = CreateOrganization("Update Document");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Administrator);
        PersonType personType = company ? PersonType.Company : PersonType.Individual;
        string firstDocument = company ? "11222333000181" : "52998224725";
        string secondDocument = company ? "12ABC34501DE35" : "11144477735";
        ClientEntity first = CreateDocumentClient(
            organization,
            "First Document",
            personType,
            firstDocument);
        ClientEntity second = CreateDocumentClient(
            organization,
            "Second Document",
            personType,
            secondDocument);
        second.Deactivate();
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [first, second]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        string apiPersonType = company ? "company" : "individual";

        using HttpResponseMessage conflictResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, first.Id),
            rawHandle,
            csrf,
            UpdateBody(
                "Copied Document",
                cpf: company ? null : secondDocument,
                personType: apiPersonType,
                cnpj: company ? secondDocument.ToLowerInvariant() : null,
                notes: "Should not persist"));

        await AssertDuplicateDocumentConflictAsync(conflictResponse);
        ClientEntity unchanged = await GetPersistedClientAsync(first.Id);
        Assert.Equal("First Document", unchanged.Name);
        Assert.Equal(firstDocument, company ? unchanged.Cnpj : unchanged.Cpf);
        Assert.Null(unchanged.Notes);

        using HttpResponseMessage keepResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, first.Id),
            rawHandle,
            csrf,
            UpdateBody(
                "Kept Document",
                cpf: company ? null : firstDocument,
                personType: apiPersonType,
                cnpj: company ? firstDocument : null,
                address: "Rua Mantida, 5",
                notes: "Persisted notes"));

        await AssertEmptyResponseAsync(keepResponse, HttpStatusCode.NoContent);
        ClientEntity kept = await GetPersistedClientAsync(first.Id);
        Assert.Equal("Kept Document", kept.Name);
        Assert.Equal(firstDocument, company ? kept.Cnpj : kept.Cpf);
        Assert.Equal("Rua Mantida, 5", kept.Address);
        Assert.Equal("Persisted notes", kept.Notes);
    }

    [Theory]
    [InlineData("personType", false)]
    [InlineData("cnpj", false)]
    [InlineData("address", false)]
    [InlineData("notes", false)]
    [InlineData("personType", true)]
    public async Task UpdateClient_MissingNewKeyOrNullPersonType_ReturnsBadRequestWithoutMutation(
        string key,
        bool sendNull)
    {
        User user = CreateUser("missing-key");
        Organization organization = CreateOrganization("Missing Key");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        var existingClient = new ClientEntity(
            organization.Id,
            "Original Keys",
            Now.AddMinutes(-1),
            cpf: "52998224725",
            address: "Original address",
            notes: "Original notes");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [existingClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Dictionary<string, object?> body = UpdateBody(
            "Changed Keys",
            "changed@example.test",
            cpf: "111.444.777-35",
            address: "Changed address",
            notes: "Changed notes");

        if (sendNull)
        {
            body[key] = null;
        }
        else
        {
            Assert.True(body.Remove(key));
        }

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, existingClient.Id),
            rawHandle,
            csrf,
            body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.Text.Json", content);
        Assert.DoesNotContain("JsonException", content);
        ClientEntity persisted = await GetPersistedClientAsync(existingClient.Id);
        Assert.Equal("Original Keys", persisted.Name);
        Assert.Null(persisted.Email);
        Assert.Equal("52998224725", persisted.Cpf);
        Assert.Equal("Original address", persisted.Address);
        Assert.Equal("Original notes", persisted.Notes);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ClientReads_MemberWithCompanyClient_DetailExposesProfileButCollectionsDoNot()
    {
        User user = CreateUser("member-company-read");
        Organization organization = CreateOrganization("Member Company Read");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        const string cnpj = "12ABC34501DE35";
        const string address = "Rua Privada Sintetica, 77";
        const string notes = "Observacao privada sintetica";
        var companyClient = new ClientEntity(
            organization.Id,
            "Company Read Client",
            Now.AddMinutes(-1),
            personType: PersonType.Company,
            cnpj: cnpj,
            address: address,
            notes: notes);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [companyClient]);

        using HttpResponseMessage detailResponse = await SendGetAsync(
            GetClientPath(organization.Id, companyClient.Id),
            rawHandle);
        using HttpResponseMessage listResponse = await SendGetAsync(
            GetClientsPath(organization.Id),
            rawHandle);
        using HttpResponseMessage lookupResponse = await SendGetAsync(
            GetClientLookupPath(organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        ClientResponse? detail = JsonSerializer.Deserialize<ClientResponse>(
            await detailResponse.Content.ReadAsStringAsync(),
            JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        Assert.Equal(ClientPersonTypeResponse.Company, detail.PersonType);
        Assert.Null(detail.Cpf);
        Assert.Equal(cnpj, detail.Cnpj);
        Assert.Equal(address, detail.Address);
        Assert.Equal(notes, detail.Notes);

        foreach (HttpResponseMessage collectionResponse in new[] { listResponse, lookupResponse })
        {
            Assert.Equal(HttpStatusCode.OK, collectionResponse.StatusCode);
            string json = await collectionResponse.Content.ReadAsStringAsync();
            Assert.Contains(companyClient.Id.ToString("D"), json);
            Assert.DoesNotContain(cnpj, json);
            Assert.DoesNotContain(address, json);
            Assert.DoesNotContain(notes, json);
            Assert.DoesNotContain("personType", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cnpj", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notes", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ClientMutations_MemberWithFullProfilePayload_ReturnForbiddenWithoutMutation()
    {
        User user = CreateUser("member-profile-mutations");
        Organization organization = CreateOrganization("Member Profile Mutations");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity existingClient = CreateClient(organization, "Member Target", 1);
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
            new
            {
                name = "Denied Company",
                personType = "company",
                cnpj = "11.222.333/0001-81",
                address = "Denied address",
                notes = "Denied notes"
            });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetClientPath(organization.Id, existingClient.Id),
            rawHandle,
            csrf,
            UpdateBody(
                "Denied Update",
                personType: "company",
                cnpj: "11.222.333/0001-81",
                address: "Denied address",
                notes: "Denied notes"));

        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
        ClientEntity persisted = await GetPersistedClientAsync(existingClient.Id);
        Assert.Equal("Member Target", persisted.Name);
        Assert.Equal(PersonType.Individual, persisted.PersonType);
        Assert.Null(persisted.Cnpj);
        Assert.Null(persisted.Address);
        Assert.Null(persisted.Notes);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Clients.CountAsync());
    }

    private static Dictionary<string, object?> UpdateBody(
        string name,
        string? email = null,
        string? phone = null,
        string? cpf = null,
        string? personType = "individual",
        string? cnpj = null,
        string? address = null,
        string? notes = null)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["email"] = email,
            ["phone"] = phone,
            ["cpf"] = cpf,
            ["personType"] = personType,
            ["cnpj"] = cnpj,
            ["address"] = address,
            ["notes"] = notes
        };
    }

    private static ClientEntity CreateDocumentClient(
        Organization organization,
        string name,
        PersonType personType,
        string document)
    {
        return personType == PersonType.Company
            ? new ClientEntity(
                organization.Id,
                name,
                Now.AddMinutes(-2),
                personType: PersonType.Company,
                cnpj: document)
            : new ClientEntity(
                organization.Id,
                name,
                Now.AddMinutes(-2),
                cpf: document);
    }

    private static void AssertDoesNotContainDocument(string content, string? document)
    {
        if (document is null)
        {
            return;
        }

        Assert.DoesNotContain(document, content, StringComparison.OrdinalIgnoreCase);
        string normalized = new(document.Where(char.IsLetterOrDigit).ToArray());
        Assert.DoesNotContain(normalized, content, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertDuplicateDocumentConflictAsync(
        HttpResponseMessage response)
    {
        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(content);
        Assert.Equal(
            "The document is already used by another client.",
            problem.RootElement.GetProperty("detail").GetString());

        foreach (string document in new[]
                 {
                     "52998224725",
                     "11144477735",
                     "11222333000181",
                     "12ABC34501DE35"
                 })
        {
            AssertDoesNotContainDocument(content, document);
        }
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
        int createdMinutesAgo,
        string? email = null,
        string? phone = null,
        string? cpf = null)
    {
        return new ClientEntity(
            organization.Id,
            name,
            Now.AddMinutes(-createdMinutesAgo),
            email,
            phone,
            cpf);
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
