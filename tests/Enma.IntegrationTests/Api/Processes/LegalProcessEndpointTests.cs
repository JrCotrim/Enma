using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Processes;
using Enma.Application.Authentication;
using Enma.Domain.Auditing;
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
                nameof(CreateLegalProcessRequest.Title),
                nameof(CreateLegalProcessRequest.ProcessNumber),
                nameof(CreateLegalProcessRequest.Status),
                nameof(CreateLegalProcessRequest.CourtOrAuthority),
                nameof(CreateLegalProcessRequest.ResponsibleMembershipId)
            ],
            GetPropertyNames<CreateLegalProcessRequest>());
        Assert.All(
            typeof(CreateLegalProcessRequest)
                .GetProperties()
                .Where(property => property.Name is not (
                    nameof(CreateLegalProcessRequest.ClientId) or
                    nameof(CreateLegalProcessRequest.Title))),
            property => Assert.False(
                property.IsDefined(
                    typeof(System.Runtime.CompilerServices.RequiredMemberAttribute),
                    inherit: false)));
        Assert.Equal(
            [nameof(UpdateLegalProcessRequest.Title)],
            GetPropertyNames<UpdateLegalProcessRequest>());
        Assert.Equal(
            [
                nameof(ChangeLegalProcessDetailsRequest.ProcessNumber),
                nameof(ChangeLegalProcessDetailsRequest.CourtOrAuthority)
            ],
            GetPropertyNames<ChangeLegalProcessDetailsRequest>());
        Assert.Equal(
            [nameof(ChangeLegalProcessStatusRequest.Status)],
            GetPropertyNames<ChangeLegalProcessStatusRequest>());
        Assert.Equal(
            [nameof(ChangeLegalProcessResponsibleRequest.ResponsibleMembershipId)],
            GetPropertyNames<ChangeLegalProcessResponsibleRequest>());
        Assert.Equal(
            [nameof(CreateLegalProcessResponse.Id)],
            GetPropertyNames<CreateLegalProcessResponse>());
        Assert.Equal(
            [
                nameof(LegalProcessResponse.Id),
                nameof(LegalProcessResponse.Title),
                nameof(LegalProcessResponse.ClientId),
                nameof(LegalProcessResponse.ClientName),
                nameof(LegalProcessResponse.CreatedAt),
                nameof(LegalProcessResponse.ProcessNumber),
                nameof(LegalProcessResponse.Status),
                nameof(LegalProcessResponse.CourtOrAuthority),
                nameof(LegalProcessResponse.ResponsibleMembershipId),
                nameof(LegalProcessResponse.ResponsibleDisplayName)
            ],
            GetPropertyNames<LegalProcessResponse>());
        Assert.Equal(
            [
                nameof(ListLegalProcessesResponse.Items),
                nameof(ListLegalProcessesResponse.PageNumber),
                nameof(ListLegalProcessesResponse.PageSize),
                nameof(ListLegalProcessesResponse.HasNext)
            ],
            GetPropertyNames<ListLegalProcessesResponse>());
        Assert.Equal(
            [
                nameof(LegalProcessLookupItemResponse.Id),
                nameof(LegalProcessLookupItemResponse.Title),
                nameof(LegalProcessLookupItemResponse.ClientName),
                nameof(LegalProcessLookupItemResponse.ProcessNumber),
                nameof(LegalProcessLookupItemResponse.Status),
                nameof(LegalProcessLookupItemResponse.ResponsibleMembershipId)
            ],
            GetPropertyNames<LegalProcessLookupItemResponse>());
        Assert.Equal(
            [
                nameof(LegalProcessLookupResponse.Items),
                nameof(LegalProcessLookupResponse.PageNumber),
                nameof(LegalProcessLookupResponse.PageSize),
                nameof(LegalProcessLookupResponse.HasNext)
            ],
            GetPropertyNames<LegalProcessLookupResponse>());

        string[] forbiddenNames =
        [
            "OrganizationId",
            "TenantId",
            "UserId",
            "Role",
            "Membership",
            "IsActive",
            "ClientIsActive",
            "NormalizedProcessNumber"
        ];
        Type[] contractTypes =
        [
            typeof(CreateLegalProcessRequest),
            typeof(UpdateLegalProcessRequest),
            typeof(ChangeLegalProcessDetailsRequest),
            typeof(ChangeLegalProcessStatusRequest),
            typeof(ChangeLegalProcessResponsibleRequest),
            typeof(CreateLegalProcessResponse),
            typeof(LegalProcessResponse),
            typeof(ListLegalProcessesResponse),
            typeof(LegalProcessLookupItemResponse),
            typeof(LegalProcessLookupResponse)
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
        using HttpResponseMessage lookupResponse = await client.GetAsync(
            GetProcessLookupPath(organizationId));
        using HttpResponseMessage getResponse = await client.GetAsync(
            GetProcessPath(organizationId, processId));
        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            GetProcessesPath(organizationId),
            new { clientId = Guid.NewGuid(), title = "Anonymous Process" });
        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync(
            GetProcessPath(organizationId, processId),
            new { title = "Anonymous Update" });

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(lookupResponse, HttpStatusCode.Unauthorized);
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
        using HttpResponseMessage lookupResponse = await SendGetAsync(
            GetProcessLookupPath(organization.Id),
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
        await AssertEmptyResponseAsync(lookupResponse, HttpStatusCode.Forbidden);
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
        using HttpResponseMessage lookupResponse = await SendGetAsync(
            GetProcessLookupPath(organization.Id),
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
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        Assert.True(lookupResponse.Headers.CacheControl?.NoStore);
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
        using HttpResponseMessage lookupAResponse = await SendGetAsync(
            GetProcessLookupPath(organizationA.Id),
            rawHandle);
        using HttpResponseMessage lookupBResponse = await SendGetAsync(
            GetProcessLookupPath(organizationB.Id),
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
            [
                "id",
                "title",
                "clientId",
                "clientName",
                "createdAt",
                "processNumber",
                "status",
                "courtOrAuthority",
                "responsibleMembershipId",
                "responsibleDisplayName"
            ],
            getDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            "inProgress",
            getDocument.RootElement.GetProperty("status").GetString());
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
        LegalProcessLookupResponse? lookupA = await lookupAResponse.Content
            .ReadFromJsonAsync<LegalProcessLookupResponse>();
        LegalProcessLookupResponse? lookupB = await lookupBResponse.Content
            .ReadFromJsonAsync<LegalProcessLookupResponse>();
        Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listBResponse.StatusCode);
        Assert.True(listAResponse.Headers.CacheControl?.NoStore);
        Assert.True(listBResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.OK, lookupAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lookupBResponse.StatusCode);
        Assert.True(lookupAResponse.Headers.CacheControl?.NoStore);
        Assert.True(lookupBResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(listA);
        Assert.NotNull(listB);
        Assert.NotNull(lookupA);
        Assert.NotNull(lookupB);
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
        LegalProcessLookupItemResponse lookupItemA = Assert.Single(lookupA.Items);
        LegalProcessLookupItemResponse lookupItemB = Assert.Single(lookupB.Items);
        Assert.Equal(processA.Id, lookupItemA.Id);
        Assert.Equal("Inactive Client A", lookupItemA.ClientName);
        Assert.Equal(processB.Id, lookupItemB.Id);
        Assert.DoesNotContain(lookupA.Items, item => item.Id == processB.Id);
        Assert.DoesNotContain(lookupB.Items, item => item.Id == processA.Id);
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
        Assert.False(defaultResult.HasNext);
        Assert.Equal([first.Id, second.Id, third.Id], defaultResult.Items.Select(
            item => item.Id));
        Assert.Equal(2, pageResult.PageNumber);
        Assert.Equal(1, pageResult.PageSize);
        Assert.True(pageResult.HasNext);
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
    public async Task LookupLegalProcesses_PaginationSearchAndMinimalResponse_ProvideCompleteDiscovery()
    {
        User user = CreateUser("lookup-pagination");
        Organization organization = CreateOrganization("Lookup Pagination");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(
            organization,
            "Lookup Client Context",
            30);
        LegalProcess[] legalProcesses = Enumerable.Range(1, 22)
            .Select(index => CreateProcess(
                organization,
                relatedClient,
                $"Lookup Process {index:D2}",
                index))
            .ToArray();
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            legalProcesses);

        using HttpResponseMessage defaultResponse = await SendGetAsync(
            GetProcessLookupPath(organization.Id),
            rawHandle);
        using HttpResponseMessage secondPageResponse = await SendGetAsync(
            $"{GetProcessLookupPath(organization.Id)}?pageNumber=2&pageSize=20",
            rawHandle);
        using HttpResponseMessage titleSearchResponse = await SendGetAsync(
            $"{GetProcessLookupPath(organization.Id)}?search=process%2022",
            rawHandle);
        using HttpResponseMessage clientSearchResponse = await SendGetAsync(
            $"{GetProcessLookupPath(organization.Id)}?search=client%20context",
            rawHandle);
        using HttpResponseMessage maximumResponse = await SendGetAsync(
            $"{GetProcessLookupPath(organization.Id)}?pageSize=100",
            rawHandle);

        LegalProcessLookupResponse? defaultResult = await defaultResponse.Content
            .ReadFromJsonAsync<LegalProcessLookupResponse>();
        LegalProcessLookupResponse? secondPageResult =
            await secondPageResponse.Content
                .ReadFromJsonAsync<LegalProcessLookupResponse>();
        string titleSearchJson = await titleSearchResponse.Content
            .ReadAsStringAsync();
        LegalProcessLookupResponse? titleSearchResult = JsonSerializer.Deserialize<
            LegalProcessLookupResponse>(
                titleSearchJson,
                JsonSerializerOptions.Web);
        LegalProcessLookupResponse? clientSearchResult =
            await clientSearchResponse.Content
                .ReadFromJsonAsync<LegalProcessLookupResponse>();
        LegalProcessLookupResponse? maximumResult = await maximumResponse.Content
            .ReadFromJsonAsync<LegalProcessLookupResponse>();

        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.True(defaultResponse.Headers.CacheControl?.NoStore);
        Assert.NotNull(defaultResult);
        Assert.Equal(1, defaultResult.PageNumber);
        Assert.Equal(20, defaultResult.PageSize);
        Assert.True(defaultResult.HasNext);
        Assert.Equal(20, defaultResult.Items.Count);

        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.NotNull(secondPageResult);
        Assert.Equal(2, secondPageResult.PageNumber);
        Assert.Equal(20, secondPageResult.PageSize);
        Assert.False(secondPageResult.HasNext);
        Assert.Equal(
            [legalProcesses[20].Id, legalProcesses[21].Id],
            secondPageResult.Items.Select(item => item.Id));

        Assert.Equal(HttpStatusCode.OK, titleSearchResponse.StatusCode);
        Assert.NotNull(titleSearchResult);
        Assert.Equal(
            legalProcesses[21].Id,
            Assert.Single(titleSearchResult.Items).Id);
        Assert.Equal(HttpStatusCode.OK, clientSearchResponse.StatusCode);
        Assert.NotNull(clientSearchResult);
        Assert.True(clientSearchResult.HasNext);
        Assert.Equal(20, clientSearchResult.Items.Count);
        Assert.Equal(HttpStatusCode.OK, maximumResponse.StatusCode);
        Assert.NotNull(maximumResult);
        Assert.Equal(100, maximumResult.PageSize);
        Assert.False(maximumResult.HasNext);
        Assert.Equal(22, maximumResult.Items.Count);

        using JsonDocument document = JsonDocument.Parse(titleSearchJson);
        Assert.Equal(
            ["items", "pageNumber", "pageSize", "hasNext"],
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            ["id", "title", "clientName", "processNumber", "status", "responsibleMembershipId"],
            document.RootElement
                .GetProperty("items")[0]
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        string[] invalidQueries =
        [
            "pageNumber=0",
            "pageNumber=-1",
            "pageSize=0",
            "pageSize=-1",
            "pageSize=101",
            "pageNumber=2147483647&pageSize=2",
            $"search={new string('x', 151)}"
        ];

        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetProcessLookupPath(organization.Id)}?{query}",
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

    [Fact]
    public async Task OperationalEndpoints_AnonymousOrWithoutCsrf_ReturnEmptyResponsesWithoutMutation()
    {
        User user = CreateUser("operational-csrf");
        Organization organization = CreateOrganization("Operational Csrf");
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

        foreach ((string path, object body) in GetOperationalRequests(
            organization.Id,
            legalProcess.Id,
            membership.Id))
        {
            using HttpResponseMessage anonymousResponse = await client.PutAsJsonAsync(
                path,
                body);
            using HttpResponseMessage missingCsrfResponse = await SendMutationAsync(
                HttpMethod.Put,
                path,
                rawHandle,
                csrf: null,
                body);
            using HttpResponseMessage invalidCsrfResponse = await SendMutationAsync(
                HttpMethod.Put,
                path,
                rawHandle,
                csrf,
                body,
                requestTokenOverride: "malformed");

            await AssertEmptyResponseAsync(
                anonymousResponse,
                HttpStatusCode.Unauthorized);
            await AssertEmptyResponseAsync(
                missingCsrfResponse,
                HttpStatusCode.BadRequest);
            await AssertEmptyResponseAsync(
                invalidCsrfResponse,
                HttpStatusCode.BadRequest);
        }

        await AssertOperationalStateUnchangedAsync(legalProcess.Id);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task OperationalEndpoints_CurrentRole_AppliesMutationPermission(
        OrganizationRole role,
        HttpStatusCode expectedStatus)
    {
        User user = CreateUser($"operational-role-{role}");
        Organization organization = CreateOrganization($"Operational Role {role}");
        Organization otherOrganization = CreateOrganization($"Operational Body {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);
        ClientEntity relatedClient = CreateClient(organization, $"{role} Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            $"{role} Process",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, otherOrganization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage detailsResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, legalProcess.Id, "details"),
            rawHandle,
            csrf,
            new
            {
                processNumber = "  0001234-56.2026.8.19.0001  ",
                courtOrAuthority = " 1ª Vara Cível ",
                organizationId = otherOrganization.Id,
                normalizedProcessNumber = "INJECTED"
            });
        using HttpResponseMessage statusResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, legalProcess.Id, "status"),
            rawHandle,
            csrf,
            new { status = "suspended", organizationId = otherOrganization.Id });
        using HttpResponseMessage responsibleResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, legalProcess.Id, "responsible"),
            rawHandle,
            csrf,
            new { responsibleMembershipId = membership.Id });

        await AssertEmptyResponseAsync(detailsResponse, expectedStatus);
        await AssertEmptyResponseAsync(statusResponse, expectedStatus);
        await AssertEmptyResponseAsync(responsibleResponse, expectedStatus);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organization.Id, legalProcess.Id),
            rawHandle);
        using HttpResponseMessage listResponse = await SendGetAsync(
            GetProcessesPath(organization.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using JsonDocument getDocument = JsonDocument.Parse(
            await getResponse.Content.ReadAsStringAsync());
        ListLegalProcessesResponse? list = await listResponse.Content
            .ReadFromJsonAsync<ListLegalProcessesResponse>();
        Assert.NotNull(list);
        LegalProcessResponse listItem = Assert.Single(list.Items);
        JsonElement root = getDocument.RootElement;

        if (expectedStatus == HttpStatusCode.NoContent)
        {
            Assert.Equal(
                "0001234-56.2026.8.19.0001",
                root.GetProperty("processNumber").GetString());
            Assert.Equal("suspended", root.GetProperty("status").GetString());
            Assert.Equal(
                "1ª Vara Cível",
                root.GetProperty("courtOrAuthority").GetString());
            Assert.Equal(
                membership.Id,
                root.GetProperty("responsibleMembershipId").GetGuid());
            Assert.Equal(
                user.Name,
                root.GetProperty("responsibleDisplayName").GetString());
            Assert.Equal("0001234-56.2026.8.19.0001", listItem.ProcessNumber);
            Assert.Equal(LegalProcessStatusResponse.Suspended, listItem.Status);
            Assert.Equal(membership.Id, listItem.ResponsibleMembershipId);
            Assert.Equal(user.Name, listItem.ResponsibleDisplayName);
            LegalProcess persisted = await GetPersistedProcessAsync(legalProcess.Id);
            Assert.Equal(organization.Id, persisted.OrganizationId);
            Assert.Equal("00012345620268190001", persisted.NormalizedProcessNumber);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("processNumber").ValueKind);
            Assert.Equal("inProgress", root.GetProperty("status").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                root.GetProperty("responsibleMembershipId").ValueKind);
            await AssertOperationalStateUnchangedAsync(legalProcess.Id);
        }
    }

    [Fact]
    public async Task OperationalEndpoints_MissingOrCrossTenantProcess_ReturnSameNotFound()
    {
        User user = CreateUser("operational-tenant");
        Organization organizationA = CreateOrganization("Operational Tenant A");
        Organization organizationB = CreateOrganization("Operational Tenant B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientA = CreateClient(organizationA, "Tenant Client A", 3);
        ClientEntity clientB = CreateClient(organizationB, "Tenant Client B", 3);
        LegalProcess processA = CreateProcess(organizationA, clientA, "Tenant A", 2);
        LegalProcess processB = CreateProcess(organizationB, clientB, "Tenant B", 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientA, clientB],
            [processA, processB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        foreach (Guid processId in new[] { processB.Id, Guid.NewGuid() })
        {
            foreach ((string path, object body) in GetOperationalRequests(
                organizationA.Id,
                processId,
                membershipA.Id))
            {
                using HttpResponseMessage response = await SendMutationAsync(
                    HttpMethod.Put,
                    path,
                    rawHandle,
                    csrf,
                    body);

                await AssertEmptyResponseAsync(response, HttpStatusCode.NotFound);
            }
        }

        using HttpResponseMessage assignForeignResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organizationA.Id, processA.Id, "responsible"),
            rawHandle,
            csrf,
            new { responsibleMembershipId = membershipB.Id });
        using HttpResponseMessage assignLocalResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organizationA.Id, processA.Id, "responsible"),
            rawHandle,
            csrf,
            new { responsibleMembershipId = membershipA.Id });
        using HttpResponseMessage crossContextGet = await SendGetAsync(
            GetProcessPath(organizationB.Id, processA.Id),
            rawHandle);
        using HttpResponseMessage listB = await SendGetAsync(
            GetProcessesPath(organizationB.Id),
            rawHandle);

        ProblemDetails foreignProblem = await AssertSafeBadRequestAsync(
            assignForeignResponse);
        Assert.Equal("Related responsible member unavailable", foreignProblem.Title);
        Assert.Equal(
            "related_responsible_unavailable",
            GetProblemExtension(foreignProblem, "code"));
        await AssertEmptyResponseAsync(assignLocalResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(crossContextGet, HttpStatusCode.NotFound);
        string listBJson = await listB.Content.ReadAsStringAsync();
        Assert.DoesNotContain(processA.Id.ToString(), listBJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(membershipA.Id.ToString(), listBJson, StringComparison.OrdinalIgnoreCase);
        await AssertOperationalStateUnchangedAsync(processB.Id);
        Assert.Equal(
            membershipA.Id,
            (await GetPersistedProcessAsync(processA.Id)).ResponsibleMembershipId);
    }

    [Fact]
    public async Task OperationalEndpoints_InvalidBodies_ReturnSafeBadRequestWithoutMutation()
    {
        User user = CreateUser("operational-validation");
        Organization organization = CreateOrganization("Operational Validation");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Validation Client", 2);
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
        string detailsPath = GetProcessOperationPath(
            organization.Id,
            legalProcess.Id,
            "details");
        string statusPath = GetProcessOperationPath(
            organization.Id,
            legalProcess.Id,
            "status");
        string responsiblePath = GetProcessOperationPath(
            organization.Id,
            legalProcess.Id,
            "responsible");

        (string Path, object Body)[] invalidRequests =
        [
            (detailsPath, new { processNumber = "ABC" }),
            (detailsPath, new { courtOrAuthority = "Court" }),
            (detailsPath, new { processNumber = new string('A', 101), courtOrAuthority = (string?)null }),
            (detailsPath, new { processNumber = (string?)null, courtOrAuthority = new string('B', 201) }),
            (statusPath, new { }),
            (statusPath, new { status = "archived" }),
            (statusPath, new { status = "InProgress" }),
            (statusPath, new { status = 2 }),
            (responsiblePath, new { }),
            (responsiblePath, new { responsibleMembershipId = Guid.Empty }),
            (responsiblePath, new { responsibleMembershipId = "not-a-guid" })
        ];

        foreach ((string path, object body) in invalidRequests)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Put,
                path,
                rawHandle,
                csrf,
                body);

            await AssertSafeBadRequestAsync(response);
        }

        foreach (string path in new[] { detailsPath, statusPath, responsiblePath })
        {
            using HttpResponseMessage response = await SendMalformedJsonAsync(
                HttpMethod.Put,
                path,
                rawHandle,
                csrf);

            await AssertSafeBadRequestAsync(response);
        }

        await AssertOperationalStateUnchangedAsync(legalProcess.Id);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task OperationalEndpoints_ConflictsAndUnavailableResponsible_ReturnNeutralProblems()
    {
        User user = CreateUser("operational-conflict");
        Organization organization = CreateOrganization("Operational Conflict");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Conflict Client", 3);
        LegalProcess firstProcess = CreateProcess(
            organization,
            relatedClient,
            "First",
            2);
        LegalProcess secondProcess = CreateProcess(
            organization,
            relatedClient,
            "Second",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [firstProcess, secondProcess]);
        User responsibleUser = CreateUser("operational-responsible");
        OrganizationMembership responsibleMembership = CreateMembership(
            responsibleUser,
            organization,
            OrganizationRole.Member);
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(responsibleUser, responsibleMembership);
            await seedContext.SaveChangesAsync();
        }

        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage firstNumber = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, firstProcess.Id, "details"),
            rawHandle,
            csrf,
            new { processNumber = "0001234-56.2026.8.19.0001", courtOrAuthority = (string?)null });
        using HttpResponseMessage duplicateNumber = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, secondProcess.Id, "details"),
            rawHandle,
            csrf,
            new { processNumber = "00012345620268190001", courtOrAuthority = (string?)null });
        using HttpResponseMessage closeFirst = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, firstProcess.Id, "status"),
            rawHandle,
            csrf,
            new { status = "closed" });
        using HttpResponseMessage suspendClosed = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, firstProcess.Id, "status"),
            rawHandle,
            csrf,
            new { status = "suspended" });
        using HttpResponseMessage assignResponsible = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, secondProcess.Id, "responsible"),
            rawHandle,
            csrf,
            new { responsibleMembershipId = responsibleMembership.Id });
        using HttpResponseMessage closeSecond = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, secondProcess.Id, "status"),
            rawHandle,
            csrf,
            new { status = "closed" });

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership = await mutationContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == responsibleMembership.Id);
            persistedMembership.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        using HttpResponseMessage reopenSecond = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, secondProcess.Id, "status"),
            rawHandle,
            csrf,
            new { status = "inProgress" });
        using HttpResponseMessage assignInactive = await SendMutationAsync(
            HttpMethod.Put,
            GetProcessOperationPath(organization.Id, firstProcess.Id, "responsible"),
            rawHandle,
            csrf,
            new { responsibleMembershipId = responsibleMembership.Id });

        await AssertEmptyResponseAsync(firstNumber, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(closeFirst, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(assignResponsible, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(closeSecond, HttpStatusCode.NoContent);
        ProblemDetails duplicateProblem = await AssertConflictProblemAsync(
            duplicateNumber);
        ProblemDetails transitionProblem = await AssertConflictProblemAsync(
            suspendClosed);
        ProblemDetails reopenProblem = await AssertConflictProblemAsync(reopenSecond);
        ProblemDetails unavailableProblem = await AssertSafeBadRequestAsync(
            assignInactive);
        Assert.Equal("Resource conflict", duplicateProblem.Title);
        Assert.Equal("Resource conflict", transitionProblem.Title);
        Assert.Equal("Resource conflict", reopenProblem.Title);
        Assert.Contains("responsible", reopenProblem.Detail, StringComparison.Ordinal);
        Assert.Equal("Related responsible member unavailable", unavailableProblem.Title);
        Assert.Equal(
            "related_responsible_unavailable",
            GetProblemExtension(unavailableProblem, "code"));

        LegalProcess persistedFirst = await GetPersistedProcessAsync(firstProcess.Id);
        LegalProcess persistedSecond = await GetPersistedProcessAsync(secondProcess.Id);
        Assert.Equal(LegalProcessStatus.Closed, persistedFirst.Status);
        Assert.Null(persistedFirst.ResponsibleMembershipId);
        Assert.Null(persistedSecond.ProcessNumber);
        Assert.Equal(LegalProcessStatus.Closed, persistedSecond.Status);
        Assert.Equal(responsibleMembership.Id, persistedSecond.ResponsibleMembershipId);
    }

    [Fact]
    public async Task OperationalMutations_AppearInAuditLogWithCodesAndWithoutValues()
    {
        User user = CreateUser("operational-audit");
        Organization organization = CreateOrganization("Operational Audit");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Audit Client", 2);
        LegalProcess legalProcess = CreateProcess(
            organization,
            relatedClient,
            "Audited",
            1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [legalProcess]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        foreach ((string path, object body) in GetOperationalRequests(
            organization.Id,
            legalProcess.Id,
            membership.Id))
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Put,
                path,
                rawHandle,
                csrf,
                body);
            await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        }

        using HttpResponseMessage auditResponse = await SendGetAsync(
            $"/api/organizations/{organization.Id:D}/audit-logs",
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        string auditJson = await auditResponse.Content.ReadAsStringAsync();
        using JsonDocument auditDocument = JsonDocument.Parse(auditJson);
        JsonElement[] items = auditDocument.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            new[]
            {
                "legal_process.details_changed",
                "legal_process.responsible_changed",
                "legal_process.status_changed"
            },
            items
                .Select(item => item.GetProperty("eventType").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal("legal_process", item.GetProperty("entityType").GetString());
            Assert.Equal(legalProcess.Id, item.GetProperty("entityId").GetGuid());
            Assert.Equal(
                item.GetProperty("eventType").GetString(),
                item.GetProperty("details").GetProperty("type").GetString());
        });
        JsonElement detailsChanged = items.Single(item =>
            item.GetProperty("eventType").GetString() == "legal_process.details_changed");
        JsonElement statusChanged = items.Single(item =>
            item.GetProperty("eventType").GetString() == "legal_process.status_changed");
        JsonElement responsibleChanged = items.Single(item =>
            item.GetProperty("eventType").GetString() ==
                "legal_process.responsible_changed");
        Assert.Equal(
            new[] { "ProcessNumber", "CourtOrAuthority" },
            detailsChanged.GetProperty("details")
                .GetProperty("changedFields")
                .EnumerateArray()
                .Select(field => field.GetString())
                .ToArray());
        Assert.Equal(
            "InProgress",
            statusChanged.GetProperty("details").GetProperty("oldStatus").GetString());
        Assert.Equal(
            "Suspended",
            statusChanged.GetProperty("details").GetProperty("newStatus").GetString());
        Assert.Equal(
            membership.Id,
            responsibleChanged.GetProperty("details")
                .GetProperty("newResponsibleMembershipId")
                .GetGuid());
        Assert.DoesNotContain("0001234", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Vara", auditJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task CreateLegalProcess_WithOperationalFields_ReturnsCreatedAndPersistsFields(
        OrganizationRole role)
    {
        User user = CreateUser($"create-operational-{role}");
        Organization organization = CreateOrganization($"Create Operational {role}");
        Organization otherOrganization = CreateOrganization(
            $"Create Operational Body {role}");
        OrganizationMembership membership = CreateMembership(user, organization, role);
        ClientEntity relatedClient = CreateClient(organization, "Operational Client", 2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, otherOrganization],
            [membership],
            [relatedClient],
            []);
        OrganizationMembership responsibleMembership = await SeedMemberAsync(
            organization,
            $"create-operational-responsible-{role}");
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = relatedClient.Id,
                title = "  Operational Create  ",
                processNumber = "  0001234-56.2026.8.19.0001  ",
                status = "closed",
                courtOrAuthority = " 1ª Vara Cível ",
                responsibleMembershipId = responsibleMembership.Id,
                normalizedProcessNumber = "INJECTED",
                organizationId = otherOrganization.Id
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);
        CreateLegalProcessResponse? created = await createResponse.Content
            .ReadFromJsonAsync<CreateLegalProcessResponse>();
        Assert.NotNull(created);
        Assert.Equal(
            GetProcessPath(organization.Id, created.Id),
            createResponse.Headers.Location?.OriginalString);
        using JsonDocument createdDocument = JsonDocument.Parse(
            await createResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            ["id"],
            createdDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetProcessPath(organization.Id, created.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using JsonDocument getDocument = JsonDocument.Parse(
            await getResponse.Content.ReadAsStringAsync());
        JsonElement root = getDocument.RootElement;
        Assert.Equal("Operational Create", root.GetProperty("title").GetString());
        Assert.Equal(
            "0001234-56.2026.8.19.0001",
            root.GetProperty("processNumber").GetString());
        Assert.Equal("closed", root.GetProperty("status").GetString());
        Assert.Equal("1ª Vara Cível", root.GetProperty("courtOrAuthority").GetString());
        Assert.Equal(
            responsibleMembership.Id,
            root.GetProperty("responsibleMembershipId").GetGuid());
        Assert.False(root.TryGetProperty("normalizedProcessNumber", out _));
        LegalProcess persisted = await GetPersistedProcessAsync(created.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal("00012345620268190001", persisted.NormalizedProcessNumber);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        AuditLog auditLog = Assert.Single(
            await dbContext.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditEventType.LegalProcessCreated, auditLog.EventType);
        Assert.Equal(created.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task CreateLegalProcess_WithOperationalBody_PreservesAuthCsrfRoleAndClientBoundaries()
    {
        User owner = CreateUser("create-boundaries-owner");
        Organization organizationA = CreateOrganization("Create Boundaries A");
        Organization organizationB = CreateOrganization("Create Boundaries B");
        OrganizationMembership ownerMembership = CreateMembership(
            owner,
            organizationA,
            OrganizationRole.Owner);
        ClientEntity clientA = CreateClient(organizationA, "Boundaries Client A", 2);
        ClientEntity clientB = CreateClient(organizationB, "Boundaries Client B", 2);
        string ownerHandle = await SeedAuthenticatedUserAsync(
            owner,
            [organizationA, organizationB],
            [ownerMembership],
            [clientA, clientB],
            []);
        User member = CreateUser("create-boundaries-member");
        OrganizationMembership memberMembership = CreateMembership(
            member,
            organizationA,
            OrganizationRole.Member);
        string memberHandle = await SeedAuthenticatedUserAsync(
            member,
            [],
            [memberMembership],
            [],
            []);
        CsrfPair ownerCsrf = await GetCsrfPairAsync(ownerHandle);
        CsrfPair memberCsrf = await GetCsrfPairAsync(memberHandle);
        object Body(Guid clientId) => new
        {
            clientId,
            title = "Boundary Process",
            processNumber = "0001234-56.2026.8.19.0001",
            status = "suspended",
            courtOrAuthority = "1ª Vara Cível",
            responsibleMembershipId = memberMembership.Id
        };

        using HttpResponseMessage anonymousResponse = await client.PostAsJsonAsync(
            GetProcessesPath(organizationA.Id),
            Body(clientA.Id));
        using HttpResponseMessage missingCsrfResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            ownerHandle,
            csrf: null,
            Body(clientA.Id));
        using HttpResponseMessage memberResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            memberHandle,
            memberCsrf,
            Body(clientA.Id));
        using HttpResponseMessage crossTenantClientResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            ownerHandle,
            ownerCsrf,
            Body(clientB.Id));

        await AssertEmptyResponseAsync(anonymousResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(missingCsrfResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(memberResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            crossTenantClientResponse,
            HttpStatusCode.NotFound);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalProcesses.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateLegalProcess_DuplicateNumber_ReturnsNeutralConflictAndOtherTenantSucceeds()
    {
        User user = CreateUser("create-duplicate");
        Organization organizationA = CreateOrganization("Create Duplicate A");
        Organization organizationB = CreateOrganization("Create Duplicate B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientA = CreateClient(organizationA, "Duplicate Client A", 2);
        ClientEntity clientB = CreateClient(organizationB, "Duplicate Client B", 2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientA, clientB],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage firstResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = clientA.Id,
                title = "First Number",
                processNumber = "0001234-56.2026.8.19.0001"
            });
        using HttpResponseMessage duplicateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationA.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = clientA.Id,
                title = "Duplicate Number",
                processNumber = "00012345620268190001",
                status = "suspended"
            });
        using HttpResponseMessage otherTenantResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetProcessesPath(organizationB.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = clientB.Id,
                title = "Other Tenant Number",
                processNumber = "0001234-56.2026.8.19.0001"
            });

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        ProblemDetails duplicateProblem = await AssertConflictProblemAsync(
            duplicateResponse);
        Assert.Equal("Resource conflict", duplicateProblem.Title);
        Assert.Null(duplicateResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.Created, otherTenantResponse.StatusCode);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.LegalProcesses.AnyAsync(
            legalProcess => legalProcess.Title == "Duplicate Number"));
        Assert.Equal(2, await dbContext.LegalProcesses.CountAsync());
        Assert.Equal(2, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateLegalProcess_UnavailableResponsible_ReturnsSameNeutralBadRequest()
    {
        User user = CreateUser("create-unavailable-responsible");
        Organization organizationA = CreateOrganization("Create Unavailable A");
        Organization organizationB = CreateOrganization("Create Unavailable B");
        OrganizationMembership membership = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organizationA, "Unavailable Client", 2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membership],
            [relatedClient],
            []);
        OrganizationMembership foreignMembership = await SeedMemberAsync(
            organizationB,
            "create-unavailable-foreign");
        OrganizationMembership inactiveMembership = await SeedMemberAsync(
            organizationA,
            "create-unavailable-inactive-membership",
            isMembershipActive: false);
        OrganizationMembership inactiveUserMembership = await SeedMemberAsync(
            organizationA,
            "create-unavailable-inactive-user",
            isUserActive: false);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        (Guid ResponsibleMembershipId, string? Status)[] cases =
        [
            (foreignMembership.Id, "inProgress"),
            (Guid.NewGuid(), null),
            (inactiveMembership.Id, "closed"),
            (inactiveUserMembership.Id, "suspended")
        ];

        foreach ((Guid responsibleMembershipId, string? status) in cases)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Post,
                GetProcessesPath(organizationA.Id),
                rawHandle,
                csrf,
                new
                {
                    clientId = relatedClient.Id,
                    title = "Unavailable Responsible",
                    status,
                    responsibleMembershipId
                });

            ProblemDetails problem = await AssertSafeBadRequestAsync(response);
            Assert.Equal("Related responsible member unavailable", problem.Title);
            Assert.Equal(
                "The requested responsible member is unavailable.",
                problem.Detail);
            Assert.Equal(
                "related_responsible_unavailable",
                GetProblemExtension(problem, "code"));
            Assert.Null(response.Headers.Location);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalProcesses.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateLegalProcess_InvalidOperationalFields_ReturnSafeBadRequestWithoutPersistence()
    {
        User user = CreateUser("create-invalid-operational");
        Organization organization = CreateOrganization("Create Invalid Operational");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Invalid Client", 2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        object[] invalidBodies =
        [
            new { clientId = relatedClient.Id, title = "Invalid", status = "archived" },
            new { clientId = relatedClient.Id, title = "Invalid", status = "InProgress" },
            new { clientId = relatedClient.Id, title = "Invalid", status = 2 },
            new
            {
                clientId = relatedClient.Id,
                title = "Invalid",
                responsibleMembershipId = Guid.Empty
            },
            new
            {
                clientId = relatedClient.Id,
                title = "Invalid",
                responsibleMembershipId = "not-a-guid"
            },
            new
            {
                clientId = relatedClient.Id,
                title = "Invalid",
                processNumber = new string('1', 101)
            },
            new
            {
                clientId = relatedClient.Id,
                title = "Invalid",
                courtOrAuthority = new string('C', 201)
            }
        ];

        foreach (object body in invalidBodies)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Post,
                GetProcessesPath(organization.Id),
                rawHandle,
                csrf,
                body);

            await AssertSafeBadRequestAsync(response);
            Assert.Null(response.Headers.Location);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalProcesses.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ListLegalProcesses_SearchFiltersSortAndHasNext_ReturnContextualResults()
    {
        User user = CreateUser("list-filters");
        Organization organizationA = CreateOrganization("List Filters A");
        Organization organizationB = CreateOrganization("List Filters B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientA = CreateClient(organizationA, "Acme Filters", 10);
        ClientEntity clientB = CreateClient(organizationB, "Acme Filters", 10);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientA, clientB],
            []);
        OrganizationMembership colleagueMembership = await SeedMemberAsync(
            organizationA,
            "list-filters-colleague");
        LegalProcess mine = CreateOperationalProcess(
            organizationA,
            clientA,
            "Alpha Mine",
            5,
            LegalProcessStatus.InProgress,
            membershipA.Id,
            "0001234-56.2026.8.19.0001");
        LegalProcess colleague = CreateOperationalProcess(
            organizationA,
            clientA,
            "Bravo Colleague",
            4,
            LegalProcessStatus.Suspended,
            colleagueMembership.Id);
        LegalProcess closedUnassigned = CreateOperationalProcess(
            organizationA,
            clientA,
            "Charlie Closed",
            3,
            LegalProcessStatus.Closed,
            null);
        LegalProcess openUnassigned = CreateOperationalProcess(
            organizationA,
            clientA,
            "Delta Open",
            2,
            LegalProcessStatus.InProgress,
            null);
        LegalProcess crossTenant = CreateOperationalProcess(
            organizationB,
            clientB,
            "Alpha Mine",
            1,
            LegalProcessStatus.InProgress,
            membershipB.Id,
            "0001234-56.2026.8.19.0001");
        await SeedProcessesAsync(
            mine,
            colleague,
            closedUnassigned,
            openUnassigned,
            crossTenant);

        async Task<ListLegalProcessesResponse> ListAsync(Guid organizationId, string query)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetProcessesPath(organizationId)}?{query}",
                rawHandle);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            string json = await response.Content.ReadAsStringAsync();

            if (organizationId == organizationA.Id)
            {
                Assert.DoesNotContain(
                    crossTenant.Id.ToString(),
                    json,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    membershipB.Id.ToString(),
                    json,
                    StringComparison.OrdinalIgnoreCase);
            }

            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal(
                ["items", "pageNumber", "pageSize", "hasNext"],
                document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray());
            return Assert.IsType<ListLegalProcessesResponse>(
                JsonSerializer.Deserialize<ListLegalProcessesResponse>(
                    json,
                    JsonSerializerOptions.Web));
        }

        async Task<Guid[]> ListIdsAsync(string query)
        {
            ListLegalProcessesResponse result = await ListAsync(organizationA.Id, query);
            return result.Items.Select(item => item.Id).ToArray();
        }

        Assert.Equal([mine.Id], await ListIdsAsync("search=00012345620268190001"));
        Assert.Equal([mine.Id], await ListIdsAsync("search=0001234-56"));
        Assert.Equal(
            [mine.Id, colleague.Id, closedUnassigned.Id, openUnassigned.Id],
            await ListIdsAsync("search=acme"));
        Assert.Equal([colleague.Id], await ListIdsAsync("status=suspended"));
        Assert.Equal([closedUnassigned.Id], await ListIdsAsync("status=closed"));
        Assert.Equal(
            [mine.Id, openUnassigned.Id],
            await ListIdsAsync("status=inProgress"));
        Assert.Equal([mine.Id], await ListIdsAsync("responsible=self"));
        Assert.Equal(
            [closedUnassigned.Id, openUnassigned.Id],
            await ListIdsAsync("responsible=unassigned"));
        Assert.Equal(
            [colleague.Id],
            await ListIdsAsync($"responsible={colleagueMembership.Id:D}"));
        Assert.Empty(await ListIdsAsync($"responsible={membershipB.Id:D}"));
        Assert.Equal(
            [mine.Id, colleague.Id, closedUnassigned.Id, openUnassigned.Id],
            await ListIdsAsync("responsible=any&sort=title"));
        Assert.Equal(
            [openUnassigned.Id, closedUnassigned.Id, colleague.Id, mine.Id],
            await ListIdsAsync("sort=newest"));

        ListLegalProcessesResponse firstPage = await ListAsync(
            organizationA.Id,
            "pageSize=3");
        ListLegalProcessesResponse lastPage = await ListAsync(
            organizationA.Id,
            "pageNumber=2&pageSize=3");
        ListLegalProcessesResponse exactPage = await ListAsync(
            organizationA.Id,
            "pageSize=4");
        Assert.True(firstPage.HasNext);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.False(lastPage.HasNext);
        Assert.Equal(openUnassigned.Id, Assert.Single(lastPage.Items).Id);
        Assert.Equal(2, lastPage.PageNumber);
        Assert.Equal(3, lastPage.PageSize);
        Assert.False(exactPage.HasNext);
        Assert.Equal(4, exactPage.Items.Count);

        ListLegalProcessesResponse selfInB = await ListAsync(
            organizationB.Id,
            "responsible=self&search=0001234");
        Assert.Equal(crossTenant.Id, Assert.Single(selfInB.Items).Id);

        string[] invalidQueries =
        [
            "status=archived",
            "status=InProgress",
            $"responsible={Guid.Empty:D}",
            "responsible=not-a-guid",
            $"responsible={colleagueMembership.Id:N}",
            "sort=oldest",
            "sort=Newest",
            $"search={new string('x', 151)}"
        ];

        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetProcessesPath(organizationA.Id)}?{query}",
                rawHandle);
            ProblemDetails problem = await AssertSafeBadRequestAsync(response);
            Assert.Equal("Invalid request data", problem.Title);
        }
    }

    [Fact]
    public async Task LookupLegalProcesses_NumberSearchAndClosedProcesses_ReturnNumberAndStatus()
    {
        User user = CreateUser("lookup-number");
        Organization organizationA = CreateOrganization("Lookup Number A");
        Organization organizationB = CreateOrganization("Lookup Number B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Member);
        ClientEntity clientA = CreateClient(organizationA, "Lookup Number Client", 10);
        ClientEntity clientB = CreateClient(organizationB, "Lookup Number Client", 10);
        LegalProcess closedNumbered = CreateOperationalProcess(
            organizationA,
            clientA,
            "Closed Numbered",
            3,
            LegalProcessStatus.Closed,
            null,
            "0001234-56.2026.8.19.0001");
        LegalProcess openUnnumbered = CreateOperationalProcess(
            organizationA,
            clientA,
            "Open Unnumbered",
            2,
            LegalProcessStatus.InProgress,
            membershipA.Id);
        LegalProcess crossTenant = CreateOperationalProcess(
            organizationB,
            clientB,
            "Closed Numbered",
            1,
            LegalProcessStatus.Closed,
            null,
            "0001234-56.2026.8.19.0001");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA],
            [clientA, clientB],
            [closedNumbered, openUnnumbered, crossTenant]);

        using HttpResponseMessage searchResponse = await SendGetAsync(
            $"{GetProcessLookupPath(organizationA.Id)}?search=00012345620268190001",
            rawHandle);
        using HttpResponseMessage allResponse = await SendGetAsync(
            GetProcessLookupPath(organizationA.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.True(searchResponse.Headers.CacheControl?.NoStore);
        using JsonDocument searchDocument = JsonDocument.Parse(
            await searchResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            ["items", "pageNumber", "pageSize", "hasNext"],
            searchDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        JsonElement item = Assert.Single(
            searchDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["id", "title", "clientName", "processNumber", "status", "responsibleMembershipId"],
            item.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(closedNumbered.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(
            "0001234-56.2026.8.19.0001",
            item.GetProperty("processNumber").GetString());
        Assert.Equal("closed", item.GetProperty("status").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            item.GetProperty("responsibleMembershipId").ValueKind);

        LegalProcessLookupResponse? all = await allResponse.Content
            .ReadFromJsonAsync<LegalProcessLookupResponse>();
        Assert.NotNull(all);
        Assert.False(all.HasNext);
        Assert.Equal(
            [
                new LegalProcessLookupItemResponse(
                    closedNumbered.Id,
                    "Closed Numbered",
                    clientA.Name,
                    "0001234-56.2026.8.19.0001",
                    LegalProcessStatusResponse.Closed,
                    null),
                new LegalProcessLookupItemResponse(
                    openUnnumbered.Id,
                    "Open Unnumbered",
                    clientA.Name,
                    null,
                    LegalProcessStatusResponse.InProgress,
                    membershipA.Id)
            ],
            all.Items);
    }

    private async Task<OrganizationMembership> SeedMemberAsync(
        Organization organization,
        string marker,
        bool isMembershipActive = true,
        bool isUserActive = true)
    {
        User user = CreateUser(marker);
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);

        if (!isMembershipActive)
        {
            membership.Deactivate();
        }

        if (!isUserActive)
        {
            user.Deactivate();
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(user, membership);
        await dbContext.SaveChangesAsync();

        return membership;
    }

    private async Task SeedProcessesAsync(params LegalProcess[] legalProcesses)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalProcesses.AddRange(legalProcesses);
        await dbContext.SaveChangesAsync();
    }

    private static LegalProcess CreateOperationalProcess(
        Organization organization,
        ClientEntity client,
        string title,
        int createdMinutesAgo,
        LegalProcessStatus status,
        Guid? responsibleMembershipId,
        string? processNumber = null)
    {
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            title,
            Now.AddMinutes(-createdMinutesAgo),
            processNumber,
            responsibleMembershipId: responsibleMembershipId);
        legalProcess.ChangeStatus(status);
        return legalProcess;
    }

    private static (string Path, object Body)[] GetOperationalRequests(
        Guid organizationId,
        Guid processId,
        Guid responsibleMembershipId)
    {
        return
        [
            (
                GetProcessOperationPath(organizationId, processId, "details"),
                new
                {
                    processNumber = "0001234-56.2026.8.19.0001",
                    courtOrAuthority = "1ª Vara Cível"
                }),
            (
                GetProcessOperationPath(organizationId, processId, "status"),
                new { status = "suspended" }),
            (
                GetProcessOperationPath(organizationId, processId, "responsible"),
                new { responsibleMembershipId })
        ];
    }

    private async Task AssertOperationalStateUnchangedAsync(Guid processId)
    {
        LegalProcess persisted = await GetPersistedProcessAsync(processId);
        Assert.Null(persisted.ProcessNumber);
        Assert.Null(persisted.NormalizedProcessNumber);
        Assert.Null(persisted.CourtOrAuthority);
        Assert.Equal(LegalProcessStatus.InProgress, persisted.Status);
        Assert.Null(persisted.ResponsibleMembershipId);
    }

    private static async Task<ProblemDetails> AssertConflictProblemAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string responseContent = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", responseContent);
        Assert.DoesNotContain("organizationId", responseContent);
        Assert.DoesNotContain("0001234", responseContent);
        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            responseContent,
            JsonSerializerOptions.Web);
        return Assert.IsType<ProblemDetails>(problemDetails);
    }

    private static string GetProcessOperationPath(
        Guid organizationId,
        Guid processId,
        string operation)
    {
        return $"{GetProcessPath(organizationId, processId)}/{operation}";
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

    private static string? GetProblemExtension(ProblemDetails problem, string name)
    {
        Assert.True(problem.Extensions.TryGetValue(name, out object? value));
        return Assert.IsType<JsonElement>(value).GetString();
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

    private static string GetProcessLookupPath(Guid organizationId)
    {
        return $"{GetProcessesPath(organizationId)}/lookup";
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
