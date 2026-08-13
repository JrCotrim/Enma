using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Processes;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Processes;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-legal-process-endpoint-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        13,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LegalProcessEndpointTests(PostgreSqlFixture fixture)
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
    public void LegalProcessContracts_CurrentScope_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(CreateLegalProcessRequest.ClientId),
                nameof(CreateLegalProcessRequest.Title)
            ],
            GetPropertyNames<CreateLegalProcessRequest>());
        Assert.Equal(
            [nameof(UpdateLegalProcessRequest.Title)],
            GetPropertyNames<UpdateLegalProcessRequest>());
        Assert.Equal(
            [nameof(CreateLegalProcessResponse.Id)],
            GetPropertyNames<CreateLegalProcessResponse>());
        Assert.Equal(
            [
                nameof(LegalProcessResponse.Id),
                nameof(LegalProcessResponse.Title),
                nameof(LegalProcessResponse.ClientId),
                nameof(LegalProcessResponse.ClientName),
                nameof(LegalProcessResponse.CreatedAt)
            ],
            GetPropertyNames<LegalProcessResponse>());
        Assert.Equal(
            [
                nameof(ListLegalProcessesResponse.Items),
                nameof(ListLegalProcessesResponse.PageNumber),
                nameof(ListLegalProcessesResponse.PageSize)
            ],
            GetPropertyNames<ListLegalProcessesResponse>());

        string[] forbiddenNames =
        [
            "OrganizationId",
            "TenantId",
            "UserId",
            "Role",
            "Membership",
            "IsActive"
        ];
        Type[] contractTypes =
        [
            typeof(CreateLegalProcessRequest),
            typeof(UpdateLegalProcessRequest),
            typeof(CreateLegalProcessResponse),
            typeof(LegalProcessResponse),
            typeof(ListLegalProcessesResponse)
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
    public async Task LegalProcessEndpoints_AnonymousRequests_ReturnEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        Guid organizationId = Guid.NewGuid();
        Guid processId = Guid.NewGuid();

        using HttpResponseMessage listResponse = await client.GetAsync(
            GetProcessesPath(organizationId));
        using HttpResponseMessage getResponse = await client.GetAsync(
            GetProcessPath(organizationId, processId));
        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            GetProcessesPath(organizationId),
            new { clientId = Guid.NewGuid(), title = "Anonymous Process" });
        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync(
            GetProcessPath(organizationId, processId),
            new { title = "Anonymous Update" });

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(getResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LegalProcessEndpoints_MissingOrganizationAccess_ReturnEmptyNoStoreForbiddenBeforeCsrf()
    {
        User user = CreateUser("organization-denied");
        Organization organization = CreateOrganization("Denied");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [],
            [],
            []);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetProcessesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organization.Id, Guid.NewGuid()),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf: null,
            new { clientId = Guid.NewGuid(), title = "Denied Create" });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, Guid.NewGuid()),
            rawHandle,
            csrf: null,
            new { title = "Denied Update" });

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(getResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalProcesses.CountAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task LegalProcessEndpoints_CurrentRole_AppliesReadAndMutationActions(
        OrganizationRole role,
        HttpStatusCode expectedMutationStatus)
    {
        User user = CreateUser($"role-{role}");
        Organization organization = CreateOrganization($"Role {role}");
        Organization otherOrganization = CreateOrganization($"Body {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);
        ClientEntity relatedClient = CreateClient(
            organization,
            $"{role} Client",
            3);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            $"{role} Original",
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, otherOrganization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle);
        using HttpResponseMessage listResponse = await SendGetAsync(
            GetProcessesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = relatedClient.Id,
                title = $"  {role} Created  ",
                organizationId = otherOrganization.Id,
                userId = Guid.NewGuid(),
                role = OrganizationRole.Owner
            });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf,
            new
            {
                title = $"{role} Updated",
                clientId = Guid.NewGuid(),
                organizationId = otherOrganization.Id
            });

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(expectedMutationStatus, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);

        if (expectedMutationStatus == HttpStatusCode.Created)
        {
            CreateLegalProcessResponse? created = await createResponse.Content
                .ReadFromJsonAsync<CreateLegalProcessResponse>();
            Assert.NotNull(created);
            Assert.Equal(
                GetProcessPath(organization.Id, created.Id),
                createResponse.Headers.Location?.OriginalString);
            using JsonDocument document = JsonDocument.Parse(
                await createResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                ["id"],
                document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray());
            await AssertEmptyResponseAsync(
                updateResponse,
                HttpStatusCode.NoContent);

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            LegalProcess persistedCreated = await dbContext.LegalProcesses
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == created.Id);
            LegalProcess persistedUpdated = await dbContext.LegalProcesses
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == legalProcess.Id);
            Assert.Equal(organization.Id, persistedCreated.OrganizationId);
            Assert.Equal(relatedClient.Id, persistedCreated.ClientId);
            Assert.Equal($"{role} Created", persistedCreated.Title);
            Assert.Equal(relatedClient.Id, persistedUpdated.ClientId);
            Assert.Equal($"{role} Updated", persistedUpdated.Title);
        }
        else
        {
            await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
            await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
            LegalProcess persisted = await GetPersistedProcessAsync(legalProcess.Id);
            Assert.Equal($"{role} Original", persisted.Title);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
        }
    }

    [Fact]
    public async Task CreateLegalProcess_UnavailableClients_ReturnSameEmptyNoStoreNotFound()
    {
        User user = CreateUser("client-oracle");
        Organization organizationA = CreateOrganization("Oracle A");
        Organization organizationB = CreateOrganization("Oracle B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        ClientEntity inactiveClientA = CreateClient(
            organizationA,
            "Inactive A",
            2);
        inactiveClientA.Deactivate();
        ClientEntity clientB = CreateClient(organizationB, "Client B", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA],
            [inactiveClientA, clientB],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage missingResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new { clientId = Guid.NewGuid(), title = "Missing" });
        using HttpResponseMessage inactiveResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new { clientId = inactiveClientA.Id, title = "Inactive" });
        using HttpResponseMessage crossTenantResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new { clientId = clientB.Id, title = "Cross Tenant" });

        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(inactiveResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossTenantResponse, HttpStatusCode.NotFound);
        Assert.Equal(
            await missingResponse.Content.ReadAsStringAsync(),
            await inactiveResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            await missingResponse.Content.ReadAsStringAsync(),
            await crossTenantResponse.Content.ReadAsStringAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task GetAndUpdateLegalProcess_MissingOrCrossTenant_ReturnSameNotFoundAndCorrectContextSucceeds()
    {
        User user = CreateUser("process-oracle");
        Organization organizationA = CreateOrganization("Process A");
        Organization organizationB = CreateOrganization("Process B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientB = CreateClient(organizationB, "B Client", 2);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "B Original",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientB],
            [processB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Guid missingProcessId = Guid.NewGuid();

        using HttpResponseMessage missingGetResponse = await SendGetAsync(
            GetProcessPath(organizationA.Id, missingProcessId),
            rawHandle);
        using HttpResponseMessage crossTenantGetResponse = await SendGetAsync(
            GetProcessPath(organizationA.Id, processB.Id),
            rawHandle);
        using HttpResponseMessage missingUpdateResponse =
            await SendMutationAsync(
                HttpMethod.Put,
                GetProcessPath(organizationA.Id, missingProcessId),
                rawHandle,
                csrf,
                new { title = "Missing Process" });
        using HttpResponseMessage crossTenantUpdateResponse =
            await SendMutationAsync(
                HttpMethod.Put,
                GetProcessPath(organizationA.Id, processB.Id),
                rawHandle,
                csrf,
                new { title = "Wrong Context" });
        using HttpResponseMessage ownGetResponse = await SendGetAsync(
            GetProcessPath(organizationB.Id, processB.Id),
            rawHandle);
        using HttpResponseMessage ownUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organizationB.Id, processB.Id),
            rawHandle,
            csrf,
            new { title = "Correct Context" });

        await AssertEmptyResponseAsync(missingGetResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(
            crossTenantGetResponse,
            HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(
            missingUpdateResponse,
            HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(
            crossTenantUpdateResponse,
            HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, ownGetResponse.StatusCode);
        await AssertEmptyResponseAsync(
            ownUpdateResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(
            "Correct Context",
            (await GetPersistedProcessAsync(processB.Id)).Title);
    }

    [Fact]
    public async Task GetListAndUpdateLegalProcess_InactiveClient_RemainAvailableAndTenantIsolated()
    {
        User user = CreateUser("inactive-client");
        Organization organizationA = CreateOrganization("Inactive A");
        Organization organizationB = CreateOrganization("Inactive B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Member);
        ClientEntity clientA = CreateClient(organizationA, "Inactive Client A", 4);
        clientA.Deactivate();
        ClientEntity clientB = CreateClient(organizationB, "Client B", 3);
        LegalProcess processA = CreateProcess(
            organizationA,
            clientA,
            "A Process",
            2);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "B Process",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientA, clientB],
            [processA, processB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organizationA.Id, processA.Id),
            rawHandle);
        using HttpResponseMessage listAResponse = await SendGetAsync(
            GetProcessesPath(organizationA.Id),
            rawHandle);
        using HttpResponseMessage listBResponse = await SendGetAsync(
            GetProcessesPath(organizationB.Id),
            rawHandle);
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organizationA.Id, processA.Id),
            rawHandle,
            csrf,
            new { title = "A Updated" });

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        string getJson = await getResponse.Content.ReadAsStringAsync();
        using JsonDocument getDocument = JsonDocument.Parse(getJson);
        Assert.Equal(
            ["id", "title", "clientId", "clientName", "createdAt"],
            getDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        LegalProcessResponse? getResult = JsonSerializer.Deserialize<
            LegalProcessResponse>(getJson, JsonSerializerOptions.Web);
        Assert.NotNull(getResult);
        Assert.Equal(processA.Id, getResult.Id);
        Assert.Equal(clientA.Id, getResult.ClientId);
        Assert.Equal(clientA.Name, getResult.ClientName);
        Assert.Equal(processA.CreatedAt, getResult.CreatedAt);

        ListLegalProcessesResponse? listA = await listAResponse.Content
            .ReadFromJsonAsync<ListLegalProcessesResponse>();
        ListLegalProcessesResponse? listB = await listBResponse.Content
            .ReadFromJsonAsync<ListLegalProcessesResponse>();
        Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listBResponse.StatusCode);
        Assert.True(listAResponse.Headers.CacheControl?.NoStore);
        Assert.True(listBResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(listA);
        Assert.NotNull(listB);
        Assert.Equal(1, listA.PageNumber);
        Assert.Equal(20, listA.PageSize);
        LegalProcessResponse itemA = Assert.Single(listA.Items);
        LegalProcessResponse itemB = Assert.Single(listB.Items);
        Assert.Equal(processA.Id, itemA.Id);
        Assert.Equal("Inactive Client A", itemA.ClientName);
        Assert.Equal(processB.Id, itemB.Id);
        Assert.Equal("Client B", itemB.ClientName);
        Assert.DoesNotContain(listA.Items, item => item.Id == processB.Id);
        Assert.DoesNotContain(listB.Items, item => item.Id == processA.Id);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
        Assert.Equal(
            "A Updated",
            (await GetPersistedProcessAsync(processA.Id)).Title);
    }

    [Fact]
    public async Task ListLegalProcesses_PaginationDefaultsBoundsAndOverflow_ReturnSafeNoStoreResponses()
    {
        User user = CreateUser("pagination");
        Organization organization = CreateOrganization("Pagination");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "List Client", 4);
        LegalProcess first = CreateProcess(
            organization,
            relatedClient,
            "Alpha",
            3);
        LegalProcess second = CreateProcess(
            organization,
            relatedClient,
            "Beta",
            2);
        LegalProcess third = CreateProcess(
            organization,
            relatedClient,
            "Gamma",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [first, second, third]);

        using HttpResponseMessage defaultResponse = await SendGetAsync(
            GetProcessesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage pageResponse = await SendGetAsync(
            $"{GetProcessesPath(organization.Id)}?pageNumber=2&pageSize=1",
            rawHandle);
        using HttpResponseMessage maximumResponse = await SendGetAsync(
            $"{GetProcessesPath(organization.Id)}?pageNumber=1&pageSize=100",
            rawHandle);

        ListLegalProcessesResponse? defaultResult = await defaultResponse.Content
            .ReadFromJsonAsync<ListLegalProcessesResponse>();
        ListLegalProcessesResponse? pageResult = await pageResponse.Content
            .ReadFromJsonAsync<ListLegalProcessesResponse>();
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, maximumResponse.StatusCode);
        Assert.NotNull(defaultResult);
        Assert.NotNull(pageResult);
        Assert.Equal(1, defaultResult.PageNumber);
        Assert.Equal(20, defaultResult.PageSize);
        Assert.Equal([first.Id, second.Id, third.Id], defaultResult.Items.Select(
            item => item.Id));
        Assert.Equal(2, pageResult.PageNumber);
        Assert.Equal(1, pageResult.PageSize);
        Assert.Equal(second.Id, Assert.Single(pageResult.Items).Id);

        string[] invalidQueries =
        [
            "pageNumber=0",
            "pageNumber=-1",
            "pageSize=0",
            "pageSize=-1",
            "pageSize=101",
            "pageNumber=2147483648",
            "pageSize=2147483648"
        ];

        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetProcessesPath(organization.Id)}?{query}",
                rawHandle);
            await AssertSafeBadRequestAsync(response);
        }
    }

    [Fact]
    public async Task LegalProcessMutations_MissingOrInvalidCsrf_ReturnBadRequestBeforeMutation()
    {
        User user = CreateUser("csrf");
        Organization organization = CreateOrganization("Csrf");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Csrf Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Original",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf: null,
            new { clientId = relatedClient.Id, title = "Missing Csrf" });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf,
            new { title = "Invalid Csrf" },
            requestTokenOverride: "malformed");

        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.BadRequest);
        Assert.Equal("Original", (await GetPersistedProcessAsync(legalProcess.Id)).Title);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task LegalProcessRequests_InvalidInput_ReturnResourceNeutralControlledBadRequestWithoutMutation()
    {
        User user = CreateUser("validation");
        Organization organization = CreateOrganization("Validation");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(
            organization,
            "Validation Client",
            2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Original",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf,
            new { clientId = relatedClient.Id, title = "   " });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf,
            new { title = new string('x', 151) });

        ProblemDetails createProblem = await AssertSafeBadRequestAsync(
            createResponse);
        ProblemDetails updateProblem = await AssertSafeBadRequestAsync(
            updateResponse);
        Assert.Equal("Invalid request data", createProblem.Title);
        Assert.Equal("Invalid request data", updateProblem.Title);
        Assert.Equal("Original", (await GetPersistedProcessAsync(legalProcess.Id)).Title);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task LegalProcessMutations_MalformedJson_ReturnSafeNoStoreBadRequestWithoutMutation()
    {
        User user = CreateUser("malformed-json");
        Organization organization = CreateOrganization("Malformed Json");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Json Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Original",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMalformedJsonAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage updateResponse = await SendMalformedJsonAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf);

        await AssertSafeBadRequestAsync(createResponse);
        await AssertSafeBadRequestAsync(updateResponse);
        Assert.Equal("Original", (await GetPersistedProcessAsync(legalProcess.Id)).Title);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task LegalProcessMutations_RoleChangesWithoutRelogin_UseLiveRole()
    {
        User user = CreateUser("live-role");
        Organization organization = CreateOrganization("Live Role");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Administrator);
        ClientEntity relatedClient = CreateClient(organization, "Live Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Original",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage allowedCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf,
            new { clientId = relatedClient.Id, title = "Admin Created" });
        Assert.Equal(HttpStatusCode.Created, allowedCreateResponse.StatusCode);

        await ChangeRoleAsync(membership.Id, OrganizationRole.Member);

        using HttpResponseMessage deniedCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf,
            new { clientId = relatedClient.Id, title = "Member Created" });
        using HttpResponseMessage deniedUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf,
            new { title = "Member Updated" });
        await AssertEmptyResponseAsync(
            deniedCreateResponse,
            HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            deniedUpdateResponse,
            HttpStatusCode.Forbidden);

        await ChangeRoleAsync(membership.Id, OrganizationRole.Owner);

        using HttpResponseMessage allowedUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle,
            csrf,
            new { title = "Owner Updated" });
        await AssertEmptyResponseAsync(
            allowedUpdateResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(
            "Owner Updated",
            (await GetPersistedProcessAsync(legalProcess.Id)).Title);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(2, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task LegalProcessEndpoints_DualMembership_UsesContextualLiveRoleWithoutRoleBleed()
    {
        User user = CreateUser("dual-role");
        Organization organizationA = CreateOrganization("Dual A");
        Organization organizationB = CreateOrganization("Dual B");
        OrganizationMembership memberA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership ownerB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientA = CreateClient(organizationA, "Client A", 4);
        ClientEntity clientB = CreateClient(organizationB, "Client B", 3);
        LegalProcess processA = CreateProcess(
            organizationA,
            clientA,
            "Process A",
            2);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "Process B",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [memberA, ownerB],
            [clientA, clientB],
            [processA, processB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage listAResponse = await SendGetAsync(
            GetProcessesPath(organizationA.Id),
            rawHandle);
        using HttpResponseMessage getAResponse = await SendGetAsync(
            GetProcessPath(organizationA.Id, processA.Id),
            rawHandle);
        using HttpResponseMessage createAResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new { clientId = clientA.Id, title = "Create A" });
        using HttpResponseMessage updateAResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organizationA.Id, processA.Id),
            rawHandle,
            csrf,
            new { title = "Update A" });
        using HttpResponseMessage createBResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationB.Id),
            rawHandle,
            csrf,
            new { clientId = clientB.Id, title = "Create B" });
        using HttpResponseMessage updateBResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessPath(organizationB.Id, processB.Id),
            rawHandle,
            csrf,
            new { title = "Update B" });

        Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getAResponse.StatusCode);
        await AssertEmptyResponseAsync(createAResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateAResponse, HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Created, createBResponse.StatusCode);
        await AssertEmptyResponseAsync(updateBResponse, HttpStatusCode.NoContent);
        Assert.Equal("Process A", (await GetPersistedProcessAsync(processA.Id)).Title);
        Assert.Equal("Update B", (await GetPersistedProcessAsync(processB.Id)).Title);
    }

    [Fact]
    public async Task LegalProcessGets_DoNotRequireCsrfAndMalformedRouteIdentifiersDoNotMatch()
    {
        User user = CreateUser("get-csrf-routes");
        Organization organization = CreateOrganization("Get Routes");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "Get Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Get Process",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetProcessesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle);
        using HttpResponseMessage malformedOrganizationResponse = await SendGetAsync(
            "/api/organizations/not-a-guid/processes",
            rawHandle);
        using HttpResponseMessage malformedProcessResponse = await SendGetAsync(
            $"{GetProcessesPath(organization.Id)}/not-a-guid",
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedOrganizationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedProcessResponse.StatusCode);
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
            $"Process HTTP {marker}",
            $"process-http-{marker}-{Guid.NewGuid():N}@example.test",
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

    private static LegalProcess CreateProcess(
        Organization organization,
        ClientEntity client,
        string title,
        int createdMinutesAgo)
    {
        return new LegalProcess(
            organization.Id,
            client.Id,
            title,
            Now.AddMinutes(-createdMinutesAgo));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<ClientEntity> clients,
        IReadOnlyCollection<LegalProcess> legalProcesses)
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
        dbContext.LegalProcesses.AddRange(legalProcesses);
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
        CsrfResponse? result = await response.Content
            .ReadFromJsonAsync<CsrfResponse>();
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

    private async Task<HttpResponseMessage> SendMalformedJsonAsync(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair csrf)
    {
        using var request = new HttpRequestMessage(method, path);
        AddCookiesAndCsrf(request, rawHandle, csrf, csrf.RequestToken);
        request.Content = new StringContent("{", Encoding.UTF8, "application/json");
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

    private async Task ChangeRoleAsync(
        Guid membershipId,
        OrganizationRole role)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == membershipId);
        membership.ChangeRole(role);
        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalProcess> GetPersistedProcessAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processId);
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

    private static async Task<ProblemDetails> AssertSafeBadRequestAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string responseContent = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", responseContent);
        Assert.DoesNotContain("stackTrace", responseContent);
        Assert.DoesNotContain("exceptionType", responseContent);
        Assert.DoesNotContain("organizationId", responseContent);
        Assert.DoesNotContain("clientName", responseContent);

        if (string.IsNullOrEmpty(responseContent))
        {
            return new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest
            };
        }

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            responseContent,
            JsonSerializerOptions.Web);
        return Assert.IsType<ProblemDetails>(problemDetails);
    }

    private static string GetProcessesPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/processes";
    }

    private static string GetProcessPath(Guid organizationId, Guid processId)
    {
        return $"{GetProcessesPath(organizationId)}/{processId:D}";
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
