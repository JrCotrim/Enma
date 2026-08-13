using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Deadlines;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Deadlines;
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

namespace Enma.IntegrationTests.Api.Deadlines;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-legal-deadline-endpoint-password-hash";

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

    public LegalDeadlineEndpointTests(PostgreSqlFixture fixture)
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
    public void LegalDeadlineContracts_CurrentScope_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(CreateLegalDeadlineRequest.ProcessId),
                nameof(CreateLegalDeadlineRequest.Title),
                nameof(CreateLegalDeadlineRequest.DueDate)
            ],
            GetPropertyNames<CreateLegalDeadlineRequest>());
        Assert.Equal(
            [
                nameof(UpdateLegalDeadlineRequest.Title),
                nameof(UpdateLegalDeadlineRequest.DueDate)
            ],
            GetPropertyNames<UpdateLegalDeadlineRequest>());
        Assert.Equal(
            [nameof(CreateLegalDeadlineResponse.Id)],
            GetPropertyNames<CreateLegalDeadlineResponse>());
        Assert.Equal(
            [
                nameof(LegalDeadlineListItemResponse.Id),
                nameof(LegalDeadlineListItemResponse.Title),
                nameof(LegalDeadlineListItemResponse.DueDate),
                nameof(LegalDeadlineListItemResponse.ProcessId),
                nameof(LegalDeadlineListItemResponse.ProcessTitle),
                nameof(LegalDeadlineListItemResponse.ClientName),
                nameof(LegalDeadlineListItemResponse.State)
            ],
            GetPropertyNames<LegalDeadlineListItemResponse>());
        Assert.Equal(
            [
                nameof(LegalDeadlineResponse.Id),
                nameof(LegalDeadlineResponse.Title),
                nameof(LegalDeadlineResponse.DueDate),
                nameof(LegalDeadlineResponse.ProcessId),
                nameof(LegalDeadlineResponse.ProcessTitle),
                nameof(LegalDeadlineResponse.ClientName),
                nameof(LegalDeadlineResponse.State),
                nameof(LegalDeadlineResponse.CreatedAt),
                nameof(LegalDeadlineResponse.CompletedAt)
            ],
            GetPropertyNames<LegalDeadlineResponse>());
        Assert.Equal(
            [
                nameof(ListLegalDeadlinesResponse.Items),
                nameof(ListLegalDeadlinesResponse.PageNumber),
                nameof(ListLegalDeadlinesResponse.PageSize)
            ],
            GetPropertyNames<ListLegalDeadlinesResponse>());
        Assert.Equal(
            typeof(DateOnly),
            typeof(CreateLegalDeadlineRequest)
                .GetProperty(nameof(CreateLegalDeadlineRequest.DueDate))?.PropertyType);
        Assert.Equal(
            typeof(DateOnly),
            typeof(LegalDeadlineResponse)
                .GetProperty(nameof(LegalDeadlineResponse.DueDate))?.PropertyType);

        string[] forbiddenNames =
        [
            "OrganizationId",
            "TenantId",
            "ClientId",
            "ClientIsActive",
            "UserId",
            "Role",
            "Membership",
            "IsCompleted",
            "IsOverdue"
        ];
        Type[] contractTypes =
        [
            typeof(CreateLegalDeadlineRequest),
            typeof(UpdateLegalDeadlineRequest),
            typeof(CreateLegalDeadlineResponse),
            typeof(LegalDeadlineListItemResponse),
            typeof(LegalDeadlineResponse),
            typeof(ListLegalDeadlinesResponse)
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
    public async Task LegalDeadlineEndpoints_AnonymousRequests_ReturnEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        Guid organizationId = Guid.NewGuid();
        Guid deadlineId = Guid.NewGuid();

        using HttpResponseMessage listResponse = await client.GetAsync(
            GetDeadlinesPath(organizationId));
        using HttpResponseMessage getResponse = await client.GetAsync(
            GetDeadlinePath(organizationId, deadlineId));
        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            GetDeadlinesPath(organizationId),
            new
            {
                processId = Guid.NewGuid(),
                title = "Anonymous Deadline",
                dueDate = "2026-11-01"
            });
        using HttpResponseMessage updateResponse = await client.PutAsJsonAsync(
            GetDeadlinePath(organizationId, deadlineId),
            new { title = "Anonymous Update", dueDate = "2026-11-02" });
        using HttpResponseMessage completeResponse = await client.PostAsync(
            GetCompletePath(organizationId, deadlineId),
            content: null);
        using HttpResponseMessage reopenResponse = await client.PostAsync(
            GetReopenPath(organizationId, deadlineId),
            content: null);

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(getResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LegalDeadlineEndpoints_MissingOrganizationAccess_ReturnEmptyNoStoreForbiddenBeforeCsrf()
    {
        User user = CreateUser("organization-denied");
        Organization organization = CreateOrganization("Denied");
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [],
            [],
            [],
            []);
        Guid deadlineId = Guid.NewGuid();

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadlineId),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf: null,
            new
            {
                processId = Guid.NewGuid(),
                title = "Denied Create",
                dueDate = "2026-11-01"
            });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadlineId),
            rawHandle,
            csrf: null,
            new { title = "Denied Update", dueDate = "2026-11-02" });
        using HttpResponseMessage completeResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadlineId),
            rawHandle,
            csrf: null);
        using HttpResponseMessage reopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadlineId),
            rawHandle,
            csrf: null);

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(getResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.Forbidden);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalDeadlines.CountAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task LegalDeadlineEndpoints_CurrentRole_ApplyReadAndMutationActions(
        OrganizationRole role,
        HttpStatusCode expectedCreateStatus)
    {
        User user = CreateUser($"role-{role}");
        Organization organization = CreateOrganization($"Role {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);
        ClientEntity relatedClient = CreateClient(
            organization,
            $"{role} Client",
            4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            $"{role} Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            $"{role} Original",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                processId = process.Id,
                title = $"{role} Created",
                dueDate = "2026-11-03"
            });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = $"{role} Updated", dueDate = "2026-11-04" });
        using HttpResponseMessage completeResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage reopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(expectedCreateStatus, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);

        if (role is OrganizationRole.Owner or OrganizationRole.Administrator)
        {
            CreateLegalDeadlineResponse? created = await createResponse.Content
                .ReadFromJsonAsync<CreateLegalDeadlineResponse>();
            Assert.NotNull(created);
            Assert.Equal(
                GetDeadlinePath(organization.Id, created.Id),
                createResponse.Headers.Location?.OriginalString);
            await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
            await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.NoContent);
            await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.NoContent);

            LegalDeadline persisted = await GetPersistedDeadlineAsync(deadline.Id);
            Assert.Equal($"{role} Updated", persisted.Title);
            Assert.Equal(new DateOnly(2026, 11, 4), persisted.DueDate);
            Assert.Null(persisted.CompletedAt);
        }
        else
        {
            await AssertEmptyResponseAsync(createResponse, HttpStatusCode.Forbidden);
            await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.Forbidden);
            await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.Forbidden);
            await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.Forbidden);

            LegalDeadline persisted = await GetPersistedDeadlineAsync(deadline.Id);
            Assert.Equal("Member Original", persisted.Title);
            Assert.Equal(new DateOnly(2026, 11, 1), persisted.DueDate);
            Assert.Null(persisted.CompletedAt);
        }
    }

    [Fact]
    public async Task CreateAndReadLegalDeadline_LeapDateAndExtraFields_PreserveCalendarDateAndServerAuthority()
    {
        User user = CreateUser("create-date-authority");
        Organization organization = CreateOrganization("Create Date");
        Organization bodyOrganization = CreateOrganization("Body Authority");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(
            organization,
            "Inactive Deadline Client",
            4);
        relatedClient.Deactivate();
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Calendar Process",
            3);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, bodyOrganization],
            [membership],
            [relatedClient],
            [process],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                processId = process.Id,
                title = "  Prazo recursal  ",
                dueDate = "2028-02-29",
                id = Guid.NewGuid(),
                deadlineId = Guid.NewGuid(),
                organizationId = bodyOrganization.Id,
                tenantId = bodyOrganization.Id,
                clientId = Guid.NewGuid(),
                createdAt = Now.AddYears(-1),
                completedAt = Now,
                status = "Completed",
                isCompleted = true,
                isOverdue = true,
                role = OrganizationRole.Owner,
                userId = Guid.NewGuid()
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);
        CreateLegalDeadlineResponse? created = await createResponse.Content
            .ReadFromJsonAsync<CreateLegalDeadlineResponse>();
        Assert.NotNull(created);
        Assert.Equal(
            GetDeadlinePath(organization.Id, created.Id),
            createResponse.Headers.Location?.OriginalString);
        using JsonDocument createDocument = JsonDocument.Parse(
            await createResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            ["id"],
            createDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        LegalDeadline persisted = await GetPersistedDeadlineAsync(created.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(process.Id, persisted.ProcessId);
        Assert.Equal("Prazo recursal", persisted.Title);
        Assert.Equal(new DateOnly(2028, 2, 29), persisted.DueDate);
        Assert.Equal(Now, persisted.CreatedAt);
        Assert.Null(persisted.CompletedAt);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, created.Id),
            rawHandle);
        using HttpResponseMessage listResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        string getJson = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"dueDate\":\"2028-02-29\"", getJson);
        Assert.DoesNotContain("2028-02-29T", getJson);
        using JsonDocument getDocument = JsonDocument.Parse(getJson);
        Assert.Equal(
            [
                "id",
                "title",
                "dueDate",
                "processId",
                "processTitle",
                "clientName",
                "state",
                "createdAt",
                "completedAt"
            ],
            getDocument.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal("Pending", getDocument.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, getDocument.RootElement
            .GetProperty("completedAt").ValueKind);
        Assert.False(getDocument.RootElement.TryGetProperty("organizationId", out _));
        Assert.False(getDocument.RootElement.TryGetProperty("clientId", out _));
        Assert.False(getDocument.RootElement.TryGetProperty("clientIsActive", out _));

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"dueDate\":\"2028-02-29\"", listJson);
        using JsonDocument listDocument = JsonDocument.Parse(listJson);
        JsonElement item = Assert.Single(
            listDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            [
                "id",
                "title",
                "dueDate",
                "processId",
                "processTitle",
                "clientName",
                "state"
            ],
            item.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("Calendar Process", item.GetProperty("processTitle").GetString());
        Assert.Equal("Inactive Deadline Client", item.GetProperty("clientName").GetString());
        Assert.Equal("Pending", item.GetProperty("state").GetString());
        Assert.False(item.TryGetProperty("completedAt", out _));
    }

    [Fact]
    public async Task CreateLegalDeadline_MissingOrCrossTenantProcess_ReturnSameEmptyNoStoreNotFound()
    {
        User user = CreateUser("process-oracle");
        Organization organizationA = CreateOrganization("Oracle A");
        Organization organizationB = CreateOrganization("Oracle B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        ClientEntity clientB = CreateClient(organizationB, "Client B", 3);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "Process B",
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA],
            [clientB],
            [processB],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage missingResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organizationA.Id),
            rawHandle,
            csrf,
            new
            {
                processId = Guid.NewGuid(),
                title = "Missing Process",
                dueDate = "2026-11-01"
            });
        using HttpResponseMessage crossTenantResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organizationA.Id),
            rawHandle,
            csrf,
            new
            {
                processId = processB.Id,
                title = "Cross Tenant Process",
                dueDate = "2026-11-01"
            });

        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossTenantResponse, HttpStatusCode.NotFound);
        Assert.Equal(
            await missingResponse.Content.ReadAsStringAsync(),
            await crossTenantResponse.Content.ReadAsStringAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalDeadlines.CountAsync());
    }

    [Fact]
    public async Task LegalDeadlineRoutes_MissingOrCrossTenantDeadline_ReturnSameNotFoundWithoutMutation()
    {
        User user = CreateUser("deadline-oracle");
        Organization organizationA = CreateOrganization("Deadline A");
        Organization organizationB = CreateOrganization("Deadline B");
        OrganizationMembership membershipA = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            user,
            organizationB,
            OrganizationRole.Owner);
        ClientEntity clientB = CreateClient(organizationB, "Client B", 4);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "Process B",
            3);
        LegalDeadline deadlineB = CreateDeadline(
            organizationB,
            processB,
            "Deadline B",
            new DateOnly(2026, 11, 1),
            2);
        deadlineB.Complete(Now.AddMinutes(-1));
        DateTimeOffset originalCompletedAt = deadlineB.CompletedAt!.Value;
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membershipA, membershipB],
            [clientB],
            [processB],
            [deadlineB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Guid missingDeadlineId = Guid.NewGuid();

        using HttpResponseMessage missingGetResponse = await SendGetAsync(
            GetDeadlinePath(organizationA.Id, missingDeadlineId),
            rawHandle);
        using HttpResponseMessage crossGetResponse = await SendGetAsync(
            GetDeadlinePath(organizationA.Id, deadlineB.Id),
            rawHandle);
        using HttpResponseMessage crossUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organizationA.Id, deadlineB.Id),
            rawHandle,
            csrf,
            new { title = "Wrong Context", dueDate = "2026-11-02" });
        using HttpResponseMessage crossCompleteResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organizationA.Id, deadlineB.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage crossReopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organizationA.Id, deadlineB.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage missingUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organizationA.Id, missingDeadlineId),
            rawHandle,
            csrf,
            new { title = "Missing", dueDate = "2026-11-02" });
        using HttpResponseMessage missingCompleteResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organizationA.Id, missingDeadlineId),
            rawHandle,
            csrf);
        using HttpResponseMessage missingReopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organizationA.Id, missingDeadlineId),
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(missingGetResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossGetResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossUpdateResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossCompleteResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossReopenResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingUpdateResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingCompleteResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingReopenResponse, HttpStatusCode.NotFound);
        Assert.Equal(
            await missingGetResponse.Content.ReadAsStringAsync(),
            await crossGetResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            await missingUpdateResponse.Content.ReadAsStringAsync(),
            await crossUpdateResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            await missingCompleteResponse.Content.ReadAsStringAsync(),
            await crossCompleteResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            await missingReopenResponse.Content.ReadAsStringAsync(),
            await crossReopenResponse.Content.ReadAsStringAsync());

        LegalDeadline persisted = await GetPersistedDeadlineAsync(deadlineB.Id);
        Assert.Equal("Deadline B", persisted.Title);
        Assert.Equal(new DateOnly(2026, 11, 1), persisted.DueDate);
        Assert.Equal(originalCompletedAt, persisted.CompletedAt);
    }

    [Fact]
    public async Task LegalDeadlineMutations_MissingOrInvalidCsrf_ReturnBadRequestBeforeAnyMutation()
    {
        User user = CreateUser("csrf");
        Organization organization = CreateOrganization("Csrf");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Csrf Client", 4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Csrf Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Original",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle);
        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf: null,
            new
            {
                processId = process.Id,
                title = "Missing Csrf",
                dueDate = "2026-11-02"
            });
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "Invalid Csrf", dueDate = "2026-11-03" },
            requestTokenOverride: "malformed");
        using HttpResponseMessage completeResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf: null);
        using HttpResponseMessage reopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            requestTokenOverride: "malformed");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        await AssertEmptyResponseAsync(createResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.BadRequest);

        LegalDeadline persisted = await GetPersistedDeadlineAsync(deadline.Id);
        Assert.Equal("Original", persisted.Title);
        Assert.Equal(new DateOnly(2026, 11, 1), persisted.DueDate);
        Assert.Null(persisted.CompletedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalDeadlines.CountAsync());
    }

    [Fact]
    public async Task LegalDeadlineRequests_InvalidDatesJsonAndApplicationInput_ReturnSafeNoStoreBadRequest()
    {
        User user = CreateUser("invalid-input");
        Organization organization = CreateOrganization("Invalid Input");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Input Client", 4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Input Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Original",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        string[] invalidDateValues =
        [
            "2027-02-29",
            "2026-11-01T00:00:00Z",
            "not-a-date"
        ];

        foreach (string invalidDate in invalidDateValues)
        {
            string json = JsonSerializer.Serialize(new
            {
                processId = process.Id,
                title = "Invalid Date",
                dueDate = invalidDate
            });
            using HttpResponseMessage response = await SendRawJsonAsync(
                HttpMethod.Post,
                GetDeadlinesPath(organization.Id),
                rawHandle,
                csrf,
                json);
            await AssertSafeBadRequestAsync(response);
        }

        using HttpResponseMessage malformedJsonResponse = await SendRawJsonAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            "{");
        using HttpResponseMessage minimumDateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "Minimum Date", dueDate = "0001-01-01" });
        using HttpResponseMessage blankTitleResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf,
            new { processId = process.Id, title = "   ", dueDate = "2026-11-02" });

        await AssertSafeBadRequestAsync(malformedJsonResponse);
        ProblemDetails minimumDateProblem = await AssertSafeBadRequestAsync(
            minimumDateResponse);
        ProblemDetails blankTitleProblem = await AssertSafeBadRequestAsync(
            blankTitleResponse);
        Assert.Equal("Invalid request data", minimumDateProblem.Title);
        Assert.Equal("Invalid request data", blankTitleProblem.Title);

        LegalDeadline persisted = await GetPersistedDeadlineAsync(deadline.Id);
        Assert.Equal("Original", persisted.Title);
        Assert.Equal(new DateOnly(2026, 11, 1), persisted.DueDate);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.LegalDeadlines.CountAsync());
    }

    [Fact]
    public async Task UpdateLegalDeadline_ExtraFields_CannotChangeOwnershipProcessOrLifecycle()
    {
        User user = CreateUser("update-authority");
        Organization organization = CreateOrganization("Update Authority");
        Organization otherOrganization = CreateOrganization("Other Authority");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Update Client", 5);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Original Process",
            4);
        ClientEntity otherClient = CreateClient(
            otherOrganization,
            "Other Client",
            3);
        LegalProcess otherProcess = CreateProcess(
            otherOrganization,
            otherClient,
            "Other Process",
            2);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Original Deadline",
            new DateOnly(2026, 11, 1),
            1);
        DateTimeOffset originalCreatedAt = deadline.CreatedAt;
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, otherOrganization],
            [membership],
            [relatedClient, otherClient],
            [process, otherProcess],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new
            {
                title = "  Novo prazo  ",
                dueDate = "2026-12-31",
                deadlineId = Guid.NewGuid(),
                organizationId = otherOrganization.Id,
                processId = otherProcess.Id,
                clientId = otherClient.Id,
                createdAt = Now.AddYears(-1),
                completedAt = Now,
                status = "Completed",
                role = OrganizationRole.Owner
            });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        LegalDeadline persisted = await GetPersistedDeadlineAsync(deadline.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(process.Id, persisted.ProcessId);
        Assert.Equal("Novo prazo", persisted.Title);
        Assert.Equal(new DateOnly(2026, 12, 31), persisted.DueDate);
        Assert.Equal(originalCreatedAt, persisted.CreatedAt);
        Assert.Null(persisted.CompletedAt);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle);
        LegalDeadlineResponse? result = await getResponse.Content
            .ReadFromJsonAsync<LegalDeadlineResponse>();
        Assert.NotNull(result);
        Assert.Equal("Novo prazo", result.Title);
        Assert.Equal(new DateOnly(2026, 12, 31), result.DueDate);
        Assert.Equal(LegalDeadlineStateResponse.Pending, result.State);
    }

    [Fact]
    public async Task UpdateCompletedDeadline_ConflictThenReopen_AllowsOfficialUpdateWorkflow()
    {
        User user = CreateUser("conflict-reopen");
        Organization organization = CreateOrganization("Conflict Reopen");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        ClientEntity relatedClient = CreateClient(organization, "Conflict Client", 4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Conflict Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Original Deadline",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage completeResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage conflictResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "Blocked Update", dueDate = "2026-12-01" });

        await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.True(conflictResponse.Headers.CacheControl?.NoStore);
        ProblemDetails? conflict = await conflictResponse.Content
            .ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(conflict);
        Assert.Equal("Resource conflict", conflict.Title);
        Assert.Equal(
            "The deadline cannot be edited in its current state.",
            conflict.Detail);
        string conflictJson = await conflictResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("completedAt", conflictJson);
        Assert.DoesNotContain("organizationId", conflictJson);
        Assert.DoesNotContain("exception", conflictJson, StringComparison.OrdinalIgnoreCase);

        LegalDeadline persistedConflict = await GetPersistedDeadlineAsync(deadline.Id);
        Assert.Equal("Original Deadline", persistedConflict.Title);
        Assert.Equal(new DateOnly(2026, 11, 1), persistedConflict.DueDate);
        Assert.Equal(Now, persistedConflict.CompletedAt);

        using HttpResponseMessage completedGetResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle);
        LegalDeadlineResponse? completed = await completedGetResponse.Content
            .ReadFromJsonAsync<LegalDeadlineResponse>();
        Assert.NotNull(completed);
        Assert.Equal(LegalDeadlineStateResponse.Completed, completed.State);
        Assert.Equal(Now, completed.CompletedAt);
        using HttpResponseMessage completedListResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);
        ListLegalDeadlinesResponse? completedList = await completedListResponse
            .Content.ReadFromJsonAsync<ListLegalDeadlinesResponse>();
        Assert.NotNull(completedList);
        Assert.Equal(
            LegalDeadlineStateResponse.Completed,
            Assert.Single(completedList.Items).State);
        Assert.DoesNotContain(
            "completedAt",
            await completedListResponse.Content.ReadAsStringAsync());

        using HttpResponseMessage reopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "  Reopened Update  ", dueDate = "2026-12-01" });

        await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);
        using HttpResponseMessage pendingGetResponse = await SendGetAsync(
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle);
        LegalDeadlineResponse? pending = await pendingGetResponse.Content
            .ReadFromJsonAsync<LegalDeadlineResponse>();
        Assert.NotNull(pending);
        Assert.Equal("Reopened Update", pending.Title);
        Assert.Equal(new DateOnly(2026, 12, 1), pending.DueDate);
        Assert.Equal(LegalDeadlineStateResponse.Pending, pending.State);
        Assert.Null(pending.CompletedAt);
    }

    [Fact]
    public async Task CompleteAndReopen_RepeatedRequests_AreIdempotentAndPreserveFirstCompletionTimestamp()
    {
        User user = CreateUser("idempotence");
        Organization organization = CreateOrganization("Idempotence");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Administrator);
        ClientEntity relatedClient = CreateClient(organization, "Lifecycle Client", 4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Lifecycle Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Lifecycle Deadline",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage firstCompleteResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(
            firstCompleteResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(Now, (await GetPersistedDeadlineAsync(deadline.Id)).CompletedAt);

        using HttpResponseMessage secondCompleteResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(
            secondCompleteResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(Now, (await GetPersistedDeadlineAsync(deadline.Id)).CompletedAt);

        using HttpResponseMessage firstReopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage secondReopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(firstReopenResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(secondReopenResponse, HttpStatusCode.NoContent);
        Assert.Null((await GetPersistedDeadlineAsync(deadline.Id)).CompletedAt);
    }

    [Fact]
    public async Task LegalDeadlineMutations_RoleChangesWithoutRelogin_UseCurrentLiveRole()
    {
        User user = CreateUser("live-role");
        Organization organization = CreateOrganization("Live Role");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "Live Client", 4);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Live Process",
            3);
        LegalDeadline deadline = CreateDeadline(
            organization,
            process,
            "Original",
            new DateOnly(2026, 11, 1),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [deadline]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage memberCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                processId = process.Id,
                title = "Member Create",
                dueDate = "2026-11-02"
            });
        await AssertEmptyResponseAsync(memberCreateResponse, HttpStatusCode.Forbidden);

        await ChangeRoleAsync(membership.Id, OrganizationRole.Administrator);

        using HttpResponseMessage adminCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                processId = process.Id,
                title = "Admin Create",
                dueDate = "2026-11-02"
            });
        using HttpResponseMessage adminUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "Admin Update", dueDate = "2026-11-03" });
        using HttpResponseMessage adminCompleteResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, deadline.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage adminReopenResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organization.Id, deadline.Id),
            rawHandle,
            csrf);

        Assert.Equal(HttpStatusCode.Created, adminCreateResponse.StatusCode);
        await AssertEmptyResponseAsync(adminUpdateResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(adminCompleteResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(adminReopenResponse, HttpStatusCode.NoContent);

        await ChangeRoleAsync(membership.Id, OrganizationRole.Member);

        using HttpResponseMessage demotedUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organization.Id, deadline.Id),
            rawHandle,
            csrf,
            new { title = "Demoted Update", dueDate = "2026-11-04" });
        await AssertEmptyResponseAsync(demotedUpdateResponse, HttpStatusCode.Forbidden);
        Assert.Equal("Admin Update", (await GetPersistedDeadlineAsync(deadline.Id)).Title);
    }

    [Fact]
    public async Task LegalDeadlineEndpoints_DualMembership_UseContextualRoleWithoutRoleBleed()
    {
        User user = CreateUser("dual-membership");
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
        ClientEntity clientA = CreateClient(organizationA, "Client A", 7);
        ClientEntity clientB = CreateClient(organizationB, "Client B", 6);
        LegalProcess processA = CreateProcess(
            organizationA,
            clientA,
            "Process A",
            5);
        LegalProcess processB = CreateProcess(
            organizationB,
            clientB,
            "Process B",
            4);
        LegalDeadline deadlineA = CreateDeadline(
            organizationA,
            processA,
            "Deadline A",
            new DateOnly(2026, 11, 1),
            3);
        LegalDeadline deadlineB = CreateDeadline(
            organizationB,
            processB,
            "Deadline B",
            new DateOnly(2026, 11, 2),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [memberA, ownerB],
            [clientA, clientB],
            [processA, processB],
            [deadlineA, deadlineB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage listAResponse = await SendGetAsync(
            GetDeadlinesPath(organizationA.Id),
            rawHandle);
        using HttpResponseMessage getAResponse = await SendGetAsync(
            GetDeadlinePath(organizationA.Id, deadlineA.Id),
            rawHandle);
        using HttpResponseMessage createAResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organizationA.Id),
            rawHandle,
            csrf,
            new
            {
                processId = processA.Id,
                title = "Create A",
                dueDate = "2026-11-03"
            });
        using HttpResponseMessage updateAResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organizationA.Id, deadlineA.Id),
            rawHandle,
            csrf,
            new { title = "Update A", dueDate = "2026-11-03" });
        using HttpResponseMessage completeAResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organizationA.Id, deadlineA.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage reopenAResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organizationA.Id, deadlineA.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage createBResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(organizationB.Id),
            rawHandle,
            csrf,
            new
            {
                processId = processB.Id,
                title = "Create B",
                dueDate = "2026-11-04"
            });
        using HttpResponseMessage updateBResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(organizationB.Id, deadlineB.Id),
            rawHandle,
            csrf,
            new { title = "Update B", dueDate = "2026-11-05" });
        using HttpResponseMessage completeBResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organizationB.Id, deadlineB.Id),
            rawHandle,
            csrf);
        using HttpResponseMessage reopenBResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(organizationB.Id, deadlineB.Id),
            rawHandle,
            csrf);

        Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getAResponse.StatusCode);
        await AssertEmptyResponseAsync(createAResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(updateAResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(completeAResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(reopenAResponse, HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Created, createBResponse.StatusCode);
        await AssertEmptyResponseAsync(updateBResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(completeBResponse, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(reopenBResponse, HttpStatusCode.NoContent);
        Assert.Equal("Deadline A", (await GetPersistedDeadlineAsync(deadlineA.Id)).Title);
        LegalDeadline persistedB = await GetPersistedDeadlineAsync(deadlineB.Id);
        Assert.Equal("Update B", persistedB.Title);
        Assert.Null(persistedB.CompletedAt);
    }

    [Fact]
    public async Task ListLegalDeadlines_PaginationAndCanonicalRoutes_ReturnExpectedSafeResponses()
    {
        User user = CreateUser("pagination-routes");
        Organization organization = CreateOrganization("Pagination Routes");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "List Client", 6);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "List Process",
            5);
        LegalDeadline first = CreateDeadline(
            organization,
            process,
            "First",
            new DateOnly(2026, 11, 1),
            4);
        LegalDeadline second = CreateDeadline(
            organization,
            process,
            "Second",
            new DateOnly(2026, 11, 2),
            3);
        LegalDeadline third = CreateDeadline(
            organization,
            process,
            "Third",
            new DateOnly(2026, 11, 3),
            2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient],
            [process],
            [first, second, third]);

        using HttpResponseMessage defaultResponse = await SendGetAsync(
            GetDeadlinesPath(organization.Id),
            rawHandle);
        using HttpResponseMessage pageResponse = await SendGetAsync(
            $"{GetDeadlinesPath(organization.Id)}?pageNumber=2&pageSize=1",
            rawHandle);
        using HttpResponseMessage maximumResponse = await SendGetAsync(
            $"{GetDeadlinesPath(organization.Id)}?pageNumber=1&pageSize=100",
            rawHandle);

        ListLegalDeadlinesResponse? defaultResult = await defaultResponse.Content
            .ReadFromJsonAsync<ListLegalDeadlinesResponse>();
        ListLegalDeadlinesResponse? pageResult = await pageResponse.Content
            .ReadFromJsonAsync<ListLegalDeadlinesResponse>();
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, maximumResponse.StatusCode);
        Assert.True(defaultResponse.Headers.CacheControl?.NoStore);
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
            "pageSize=101",
            "pageNumber=2147483648",
            "pageSize=2147483648"
        ];

        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetDeadlinesPath(organization.Id)}?{query}",
                rawHandle);
            await AssertSafeBadRequestAsync(response);
        }

        using HttpResponseMessage globalResponse = await SendGetAsync(
            $"/api/deadlines/{first.Id:D}",
            rawHandle);
        using HttpResponseMessage processNestedResponse = await SendGetAsync(
            $"/api/organizations/{organization.Id:D}/processes/{process.Id:D}/deadlines",
            rawHandle);
        using HttpResponseMessage malformedOrganizationResponse = await SendGetAsync(
            "/api/organizations/not-a-guid/deadlines",
            rawHandle);
        using HttpResponseMessage malformedDeadlineResponse = await SendGetAsync(
            $"{GetDeadlinesPath(organization.Id)}/not-a-guid",
            rawHandle);

        Assert.Equal(HttpStatusCode.NotFound, globalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, processNestedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedOrganizationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedDeadlineResponse.StatusCode);
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
            $"Deadline HTTP {marker}",
            $"deadline-http-{marker}-{Guid.NewGuid():N}@example.test",
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

    private static LegalDeadline CreateDeadline(
        Organization organization,
        LegalProcess process,
        string title,
        DateOnly dueDate,
        int createdMinutesAgo)
    {
        return new LegalDeadline(
            organization.Id,
            process.Id,
            title,
            dueDate,
            Now.AddMinutes(-createdMinutesAgo));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<ClientEntity> clients,
        IReadOnlyCollection<LegalProcess> legalProcesses,
        IReadOnlyCollection<LegalDeadline> legalDeadlines)
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
        dbContext.LegalDeadlines.AddRange(legalDeadlines);
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

    private async Task<HttpResponseMessage> SendRawJsonAsync(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair csrf,
        string json)
    {
        using var request = new HttpRequestMessage(method, path);
        AddCookiesAndCsrf(request, rawHandle, csrf, csrf.RequestToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
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

    private async Task<LegalDeadline> GetPersistedDeadlineAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineId);
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
        Assert.DoesNotContain("processTitle", responseContent);

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

    private static string GetDeadlinesPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/deadlines";
    }

    private static string GetDeadlinePath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinesPath(organizationId)}/{deadlineId:D}";
    }

    private static string GetCompletePath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinePath(organizationId, deadlineId)}/complete";
    }

    private static string GetReopenPath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinePath(organizationId, deadlineId)}/reopen";
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
