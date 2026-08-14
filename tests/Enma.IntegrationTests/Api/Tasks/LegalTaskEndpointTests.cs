using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Tasks;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Tasks;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash = "synthetic-legal-task-http-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        14,
        15,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LegalTaskEndpointTests(PostgreSqlFixture fixture)
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
    public void LegalTaskContracts_CurrentScope_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(CreateLegalTaskRequest.Title),
                nameof(CreateLegalTaskRequest.Description),
                nameof(CreateLegalTaskRequest.DueDate),
                nameof(CreateLegalTaskRequest.ProcessId),
                nameof(CreateLegalTaskRequest.AssigneeMembershipId)
            ],
            GetPropertyNames<CreateLegalTaskRequest>());
        Assert.Equal(
            [
                nameof(UpdateLegalTaskRequest.Title),
                nameof(UpdateLegalTaskRequest.Description),
                nameof(UpdateLegalTaskRequest.DueDate),
                nameof(UpdateLegalTaskRequest.ProcessId)
            ],
            GetPropertyNames<UpdateLegalTaskRequest>());
        Assert.Equal(
            [nameof(ChangeLegalTaskAssigneeRequest.AssigneeMembershipId)],
            GetPropertyNames<ChangeLegalTaskAssigneeRequest>());
        Assert.Equal(
            [nameof(CreateLegalTaskResponse.Id)],
            GetPropertyNames<CreateLegalTaskResponse>());
        Assert.Equal(
            [
                nameof(LegalTaskResponse.Id),
                nameof(LegalTaskResponse.Title),
                nameof(LegalTaskResponse.Description),
                nameof(LegalTaskResponse.DueDate),
                nameof(LegalTaskResponse.ProcessId),
                nameof(LegalTaskResponse.ProcessTitle),
                nameof(LegalTaskResponse.ClientName),
                nameof(LegalTaskResponse.AssigneeMembershipId),
                nameof(LegalTaskResponse.AssigneeDisplayName),
                nameof(LegalTaskResponse.CreatedByMembershipId),
                nameof(LegalTaskResponse.CreatedByDisplayName),
                nameof(LegalTaskResponse.State),
                nameof(LegalTaskResponse.CreatedAt),
                nameof(LegalTaskResponse.CompletedAt)
            ],
            GetPropertyNames<LegalTaskResponse>());

        string[] forbiddenRequestNames =
        [
            "OrganizationId",
            "ClientId",
            "CreatedByMembershipId",
            "UserId",
            "Role",
            "OrganizationRole",
            "State",
            "CompletedAt",
            "CreatedAt"
        ];

        foreach (Type requestType in new[]
        {
            typeof(CreateLegalTaskRequest),
            typeof(UpdateLegalTaskRequest),
            typeof(ChangeLegalTaskAssigneeRequest)
        })
        {
            Assert.DoesNotContain(
                requestType.GetProperties(),
                property => forbiddenRequestNames.Contains(property.Name));
        }

        Assert.Equal(
            typeof(DateOnly?),
            typeof(CreateLegalTaskRequest)
                .GetProperty(nameof(CreateLegalTaskRequest.DueDate))?.PropertyType);
        Assert.Equal(
            typeof(DateOnly?),
            typeof(LegalTaskResponse)
                .GetProperty(nameof(LegalTaskResponse.DueDate))?.PropertyType);
    }

    [Fact]
    public async Task LegalTaskEndpoints_AnonymousRequests_ReturnNoStoreUnauthorizedBeforeCsrf()
    {
        Guid organizationId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();

        HttpResponseMessage[] responses =
        [
            await client.GetAsync(GetTasksPath(organizationId)),
            await client.GetAsync(GetTaskPath(organizationId, taskId)),
            await client.PostAsJsonAsync(
                GetTasksPath(organizationId),
                new { title = "Anonymous" }),
            await client.PutAsJsonAsync(
                GetTaskPath(organizationId, taskId),
                new { title = "Anonymous" }),
            await client.PutAsJsonAsync(
                GetAssigneePath(organizationId, taskId),
                new { assigneeMembershipId = (Guid?)null }),
            await client.PostAsync(GetCompletePath(organizationId, taskId), null),
            await client.PostAsync(GetReopenPath(organizationId, taskId), null)
        ];

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
            }
        }
    }

    [Fact]
    public async Task LegalTaskEndpoints_MissingOrganizationAccess_ReturnNoStoreForbiddenBeforeCsrf()
    {
        User actor = CreateUser("organization-denied");
        Organization organization = CreateOrganization("Denied");
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [],
            [],
            [],
            []);
        Guid taskId = Guid.NewGuid();

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetTasksPath(organization.Id),
            rawHandle);
        using HttpResponseMessage getResponse = await SendGetAsync(
            GetTaskPath(organization.Id, taskId),
            rawHandle);
        using HttpResponseMessage mutationResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetTasksPath(organization.Id),
            rawHandle,
            csrf: null,
            new { title = "Denied" });

        await AssertEmptyResponseAsync(listResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(getResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(mutationResponse, HttpStatusCode.Forbidden);
        Assert.Equal(0, await CountTasksAsync());
    }

    [Fact]
    public async Task LegalTaskCreateGetList_ValidRequest_UsesServerAuthorityAndExactJsonContract()
    {
        User actor = CreateUser("actor");
        User assignee = CreateUser("assignee");
        Organization organization = CreateOrganization("Contract");
        Organization otherOrganization = CreateOrganization("Hostile");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        OrganizationMembership assigneeMembership = CreateMembership(
            assignee,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "Client Contract", 5);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Process Contract",
            4);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [assignee],
            [organization, otherOrganization],
            [actorMembership, assigneeMembership],
            [relatedClient],
            [process],
            []);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetTasksPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                title = "  Prepare response  ",
                description = "  Review documents.  ",
                dueDate = "2026-08-20",
                processId = process.Id,
                assigneeMembershipId = assigneeMembership.Id,
                organizationId = otherOrganization.Id,
                userId = assignee.Id,
                createdByMembershipId = assigneeMembership.Id,
                role = "Owner",
                state = "completed",
                completedAt = Now.AddDays(-1),
                createdAt = Now.AddYears(-1),
                clientId = Guid.NewGuid()
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);
        CreateLegalTaskResponse? created = await createResponse.Content
            .ReadFromJsonAsync<CreateLegalTaskResponse>();
        Assert.NotNull(created);
        Assert.Equal(
            GetTaskPath(organization.Id, created.Id),
            createResponse.Headers.Location?.OriginalString);

        LegalTask persisted = await GetPersistedTaskAsync(created.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(actorMembership.Id, persisted.CreatedByMembershipId);
        Assert.Equal(assigneeMembership.Id, persisted.AssigneeMembershipId);
        Assert.Equal("Prepare response", persisted.Title);
        Assert.Equal("Review documents.", persisted.Description);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(Now, persisted.CreatedAt);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetTaskPath(organization.Id, created.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        string getJson = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"dueDate\":\"2026-08-20\"", getJson);
        Assert.DoesNotContain("2026-08-20T", getJson);
        using JsonDocument getDocument = JsonDocument.Parse(getJson);
        Assert.Equal(
            [
                "id",
                "title",
                "description",
                "dueDate",
                "processId",
                "processTitle",
                "clientName",
                "assigneeMembershipId",
                "assigneeDisplayName",
                "createdByMembershipId",
                "createdByDisplayName",
                "state",
                "createdAt",
                "completedAt"
            ],
            getDocument.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal("pending", getDocument.RootElement.GetProperty("state").GetString());
        Assert.Equal(actor.Name, getDocument.RootElement
            .GetProperty("createdByDisplayName").GetString());
        Assert.False(getDocument.RootElement.TryGetProperty("organizationId", out _));
        Assert.False(getDocument.RootElement.TryGetProperty("email", out _));
        Assert.False(getDocument.RootElement.TryGetProperty("role", out _));

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetTasksPath(organization.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        ListLegalTasksResponse? list = await listResponse.Content
            .ReadFromJsonAsync<ListLegalTasksResponse>();
        Assert.NotNull(list);
        Assert.Equal(1, list.PageNumber);
        Assert.Equal(20, list.PageSize);
        Assert.False(list.HasNext);
        Assert.Equal(created.Id, Assert.Single(list.Items).Id);
    }

    [Fact]
    public async Task LegalTaskMutations_MissingOrInvalidCsrf_AllRoutesRejectWithoutMutation()
    {
        User actor = CreateUser("csrf");
        Organization organization = CreateOrganization("Csrf");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            organization,
            membership,
            "Original",
            createdMinutesAgo: 10);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            [legalTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        (HttpMethod Method, string Path, object? Body)[] mutations =
        [
            (HttpMethod.Post, GetTasksPath(organization.Id), new { title = "Create" }),
            (HttpMethod.Put, GetTaskPath(organization.Id, legalTask.Id), new { title = "Update" }),
            (HttpMethod.Put, GetAssigneePath(organization.Id, legalTask.Id), new { assigneeMembershipId = (Guid?)null }),
            (HttpMethod.Post, GetCompletePath(organization.Id, legalTask.Id), null),
            (HttpMethod.Post, GetReopenPath(organization.Id, legalTask.Id), null)
        ];

        foreach ((HttpMethod method, string path, object? body) in mutations)
        {
            using HttpResponseMessage missing = await SendMutationAsync(
                method,
                path,
                rawHandle,
                csrf: null,
                body);
            await AssertEmptyResponseAsync(missing, HttpStatusCode.BadRequest);

            using HttpResponseMessage invalid = await SendMutationAsync(
                method,
                path,
                rawHandle,
                csrf,
                body,
                requestTokenOverride: "invalid-token");
            await AssertEmptyResponseAsync(invalid, HttpStatusCode.BadRequest);
        }

        LegalTask persisted = await GetPersistedTaskAsync(legalTask.Id);
        Assert.Equal("Original", persisted.Title);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(1, await CountTasksAsync());
    }

    [Fact]
    public async Task LegalTaskUpdateAndAssignment_NullClearsValuesAndMissingAssigneePropertyIsBadRequest()
    {
        User actor = CreateUser("clear");
        User assignee = CreateUser("clear-assignee");
        Organization organization = CreateOrganization("Clear");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        OrganizationMembership assigneeMembership = CreateMembership(
            assignee,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "Clear Client", 5);
        LegalProcess process = CreateProcess(organization, relatedClient, "Clear Process", 4);
        LegalTask legalTask = CreateTask(
            organization,
            actorMembership,
            "Before",
            "Description",
            new DateOnly(2026, 8, 20),
            process.Id,
            assigneeMembership.Id,
            3);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [assignee],
            [organization],
            [actorMembership, assigneeMembership],
            [relatedClient],
            [process],
            [legalTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetTaskPath(organization.Id, legalTask.Id),
            rawHandle,
            csrf,
            new
            {
                title = "After",
                description = (string?)null,
                dueDate = (DateOnly?)null,
                processId = (Guid?)null,
                assigneeMembershipId = actorMembership.Id,
                createdByMembershipId = assigneeMembership.Id,
                completedAt = Now,
                organizationId = Guid.NewGuid(),
                role = "Owner",
                clientId = Guid.NewGuid()
            });
        await AssertEmptyResponseAsync(updateResponse, HttpStatusCode.NoContent);

        LegalTask updated = await GetPersistedTaskAsync(legalTask.Id);
        Assert.Equal("After", updated.Title);
        Assert.Null(updated.Description);
        Assert.Null(updated.DueDate);
        Assert.Null(updated.ProcessId);
        Assert.Equal(assigneeMembership.Id, updated.AssigneeMembershipId);
        Assert.Equal(actorMembership.Id, updated.CreatedByMembershipId);
        Assert.Null(updated.CompletedAt);

        using HttpResponseMessage unassignResponse = await SendRawJsonAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, legalTask.Id),
            rawHandle,
            csrf,
            "{\"assigneeMembershipId\":null,\"userId\":\"00000000-0000-0000-0000-000000000001\",\"role\":\"Owner\",\"state\":\"completed\"}");
        await AssertEmptyResponseAsync(unassignResponse, HttpStatusCode.NoContent);
        Assert.Null((await GetPersistedTaskAsync(legalTask.Id)).AssigneeMembershipId);

        using HttpResponseMessage missingResponse = await SendRawJsonAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, legalTask.Id),
            rawHandle,
            csrf,
            "{}");
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.True(missingResponse.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("?state=banana")]
    [InlineData("?state=all")]
    [InlineData("?processId=abc")]
    [InlineData("?processId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?assignee=banana")]
    [InlineData("?assignee=00000000-0000-0000-0000-000000000000")]
    [InlineData("?assignee=user:00000000-0000-0000-0000-000000000001")]
    [InlineData("?pageNumber=abc")]
    [InlineData("?pageSize=abc")]
    [InlineData("?pageNumber=0")]
    [InlineData("?pageNumber=-1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?pageNumber=2147483647&pageSize=100")]
    public async Task LegalTaskList_InvalidQuery_ReturnsSafeNoStoreBadRequest(string query)
    {
        User actor = CreateUser("invalid-query");
        Organization organization = CreateOrganization("Invalid Query");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            []);

        using HttpResponseMessage response = await SendGetAsync(
            GetTasksPath(organization.Id) + query,
            rawHandle);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(
            "exception",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegalTaskList_ApprovedFiltersDefaultsOrderingAndPagination_PreserveApplicationSemantics()
    {
        User actor = CreateUser("filters");
        User other = CreateUser("filters-other");
        Organization organization = CreateOrganization("Filters");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        OrganizationMembership otherMembership = CreateMembership(
            other,
            organization,
            OrganizationRole.Member);
        LegalTask selfDated = CreateTask(
            organization,
            actorMembership,
            "Self dated",
            dueDate: new DateOnly(2026, 8, 19),
            assigneeMembershipId: actorMembership.Id,
            createdMinutesAgo: 5);
        LegalTask otherNull = CreateTask(
            organization,
            actorMembership,
            "Other null",
            assigneeMembershipId: otherMembership.Id,
            createdMinutesAgo: 4);
        LegalTask unassigned = CreateTask(
            organization,
            actorMembership,
            "Unassigned",
            dueDate: new DateOnly(2026, 8, 20),
            createdMinutesAgo: 3);
        LegalTask completed = CreateTask(
            organization,
            actorMembership,
            "Completed",
            assigneeMembershipId: actorMembership.Id,
            createdMinutesAgo: 6);
        completed.Complete(Now.AddMinutes(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [other],
            [organization],
            [actorMembership, otherMembership],
            [],
            [],
            [selfDated, otherNull, unassigned, completed]);

        using HttpResponseMessage defaultsResponse = await SendGetAsync(
            GetTasksPath(organization.Id),
            rawHandle);
        ListLegalTasksResponse defaults = Assert.IsType<ListLegalTasksResponse>(
            await defaultsResponse.Content.ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Equal(
            [selfDated.Id, unassigned.Id, otherNull.Id],
            defaults.Items.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(defaults.Items, item => item.Id == completed.Id);

        await AssertListIdsAsync(
            $"?assignee=self",
            rawHandle,
            organization.Id,
            selfDated.Id);
        await AssertListIdsAsync(
            $"?assignee=unassigned",
            rawHandle,
            organization.Id,
            unassigned.Id);
        await AssertListIdsAsync(
            $"?assignee={otherMembership.Id:D}",
            rawHandle,
            organization.Id,
            otherNull.Id);
        await AssertListIdsAsync(
            "?state=COMPLETED",
            rawHandle,
            organization.Id,
            completed.Id);

        using HttpResponseMessage pageResponse = await SendGetAsync(
            GetTasksPath(organization.Id) + "?assignee=any&pageNumber=1&pageSize=2",
            rawHandle);
        ListLegalTasksResponse page = Assert.IsType<ListLegalTasksResponse>(
            await pageResponse.Content.ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasNext);

        using HttpResponseMessage maximumResponse = await SendGetAsync(
            GetTasksPath(organization.Id) + "?pageSize=100",
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, maximumResponse.StatusCode);

        using HttpResponseMessage oracleResponse = await SendGetAsync(
            GetTasksPath(organization.Id) +
                $"?processId={Guid.NewGuid():D}&assignee={Guid.NewGuid():D}",
            rawHandle);
        ListLegalTasksResponse oracle = Assert.IsType<ListLegalTasksResponse>(
            await oracleResponse.Content.ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Empty(oracle.Items);
    }

    [Fact]
    public async Task LegalTaskRequests_InvalidJsonDatesRequiredMembersAndRoutes_ReturnControlledErrors()
    {
        User actor = CreateUser("binding");
        Organization organization = CreateOrganization("Binding");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            organization,
            membership,
            "Binding task",
            createdMinutesAgo: 2);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            [legalTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        string[] invalidCreateBodies =
        [
            "{",
            "{}",
            "{\"title\":\"Invalid date\",\"dueDate\":\"2026-02-30\"}",
            "{\"title\":\"Timestamp\",\"dueDate\":\"2026-08-20T00:00:00Z\"}",
            "{\"title\":\"\"}"
        ];

        foreach (string body in invalidCreateBodies)
        {
            using HttpResponseMessage response = await SendRawJsonAsync(
                HttpMethod.Post,
                GetTasksPath(organization.Id),
                rawHandle,
                csrf,
                body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        using HttpResponseMessage malformedOrganization = await SendGetAsync(
            "/api/organizations/not-a-guid/tasks",
            rawHandle);
        using HttpResponseMessage malformedTask = await SendGetAsync(
            GetTasksPath(organization.Id) + "/not-a-guid",
            rawHandle);
        using HttpResponseMessage emptyTask = await SendGetAsync(
            GetTaskPath(organization.Id, Guid.Empty),
            rawHandle);
        Assert.Equal(HttpStatusCode.NotFound, malformedOrganization.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedTask.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyTask.StatusCode);
    }

    [Fact]
    public async Task LegalTaskTenantIsolation_GetAndMutation_ConvergeToNotFoundAndDoNotMutateOtherTenant()
    {
        User actor = CreateUser("tenant");
        User otherCreator = CreateUser("tenant-other");
        Organization organizationA = CreateOrganization("Tenant A");
        Organization organizationB = CreateOrganization("Tenant B");
        OrganizationMembership membershipA = CreateMembership(
            actor,
            organizationA,
            OrganizationRole.Owner);
        OrganizationMembership membershipB = CreateMembership(
            otherCreator,
            organizationB,
            OrganizationRole.Owner);
        LegalTask taskA = CreateTask(
            organizationA,
            membershipA,
            "Task A",
            createdMinutesAgo: 2);
        LegalTask taskB = CreateTask(
            organizationB,
            membershipB,
            "Task B",
            createdMinutesAgo: 1);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [otherCreator],
            [organizationA, organizationB],
            [membershipA, membershipB],
            [],
            [],
            [taskA, taskB]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage ownResponse = await SendGetAsync(
            GetTaskPath(organizationA.Id, taskA.Id),
            rawHandle);
        using HttpResponseMessage crossGetResponse = await SendGetAsync(
            GetTaskPath(organizationA.Id, taskB.Id),
            rawHandle);
        using HttpResponseMessage crossUpdateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetTaskPath(organizationA.Id, taskB.Id),
            rawHandle,
            csrf,
            new { title = "Leaked update" });

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        await AssertEmptyResponseAsync(crossGetResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(crossUpdateResponse, HttpStatusCode.NotFound);
        Assert.Equal("Task B", (await GetPersistedTaskAsync(taskB.Id)).Title);
    }

    [Fact]
    public async Task LegalTaskLifecycle_AuthorizedOperationsAreIdempotentAndCompletedMutationsConflict()
    {
        User actor = CreateUser("lifecycle");
        Organization organization = CreateOrganization("Lifecycle");
        OrganizationMembership membership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        LegalTask legalTask = CreateTask(
            organization,
            membership,
            "Lifecycle task",
            assigneeMembershipId: membership.Id,
            createdMinutesAgo: 10);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [],
            [organization],
            [membership],
            [],
            [],
            [legalTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        for (int index = 0; index < 2; index++)
        {
            using HttpResponseMessage completeResponse = await SendMutationAsync(
                HttpMethod.Post,
                GetCompletePath(organization.Id, legalTask.Id),
                rawHandle,
                csrf);
            await AssertEmptyResponseAsync(completeResponse, HttpStatusCode.NoContent);
        }

        Assert.Equal(Now, (await GetPersistedTaskAsync(legalTask.Id)).CompletedAt);

        using HttpResponseMessage updateResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetTaskPath(organization.Id, legalTask.Id),
            rawHandle,
            csrf,
            new { title = "Cannot update" });
        Assert.Equal(HttpStatusCode.Conflict, updateResponse.StatusCode);
        Assert.True(updateResponse.Headers.CacheControl?.NoStore);
        string conflictContent = await updateResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("exception", conflictContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completedAt", conflictContent);

        using HttpResponseMessage assignmentResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, legalTask.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Conflict, assignmentResponse.StatusCode);

        for (int index = 0; index < 2; index++)
        {
            using HttpResponseMessage reopenResponse = await SendMutationAsync(
                HttpMethod.Post,
                GetReopenPath(organization.Id, legalTask.Id),
                rawHandle,
                csrf);
            await AssertEmptyResponseAsync(reopenResponse, HttpStatusCode.NoContent);
        }

        Assert.Null((await GetPersistedTaskAsync(legalTask.Id)).CompletedAt);
    }

    [Fact]
    public async Task LegalTaskMemberAndAdministratorAuthorization_UsesLiveContextualMembership()
    {
        User actor = CreateUser("role-actor");
        User other = CreateUser("role-other");
        Organization organization = CreateOrganization("Roles");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Member);
        OrganizationMembership otherMembership = CreateMembership(
            other,
            organization,
            OrganizationRole.Member);
        LegalTask ownTask = CreateTask(
            organization,
            actorMembership,
            "Own task",
            createdMinutesAgo: 5);
        LegalTask otherTask = CreateTask(
            organization,
            otherMembership,
            "Other task",
            assigneeMembershipId: otherMembership.Id,
            createdMinutesAgo: 4);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [other],
            [organization],
            [actorMembership, otherMembership],
            [],
            [],
            [ownTask, otherTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createSelfResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetTasksPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                title = "Member self",
                assigneeMembershipId = actorMembership.Id
            });
        Assert.Equal(HttpStatusCode.Created, createSelfResponse.StatusCode);

        using HttpResponseMessage createUnassignedResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetTasksPath(organization.Id),
            rawHandle,
            csrf,
            new { title = "Member unassigned" });
        Assert.Equal(HttpStatusCode.Created, createUnassignedResponse.StatusCode);

        using HttpResponseMessage createOtherResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetTasksPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                title = "Forbidden member target",
                assigneeMembershipId = otherMembership.Id
            });
        await AssertEmptyResponseAsync(createOtherResponse, HttpStatusCode.Forbidden);

        using HttpResponseMessage updateOwnResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetTaskPath(organization.Id, ownTask.Id),
            rawHandle,
            csrf,
            new { title = "Own task updated" });
        await AssertEmptyResponseAsync(updateOwnResponse, HttpStatusCode.NoContent);

        using HttpResponseMessage updateOtherResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetTaskPath(organization.Id, otherTask.Id),
            rawHandle,
            csrf,
            new { title = "Forbidden update" });
        await AssertEmptyResponseAsync(updateOtherResponse, HttpStatusCode.Forbidden);

        using HttpResponseMessage claimResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, ownTask.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = actorMembership.Id });
        await AssertEmptyResponseAsync(claimResponse, HttpStatusCode.NoContent);

        using HttpResponseMessage releaseResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, ownTask.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = (Guid?)null });
        await AssertEmptyResponseAsync(releaseResponse, HttpStatusCode.NoContent);

        using HttpResponseMessage assignOtherResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, ownTask.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = otherMembership.Id });
        await AssertEmptyResponseAsync(assignOtherResponse, HttpStatusCode.Forbidden);

        using HttpResponseMessage completeOtherResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(organization.Id, otherTask.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(completeOtherResponse, HttpStatusCode.Forbidden);

        await ChangeRoleAsync(actorMembership.Id, OrganizationRole.Administrator);
        using HttpResponseMessage administratorAssignResponse = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(organization.Id, ownTask.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = otherMembership.Id });
        await AssertEmptyResponseAsync(
            administratorAssignResponse,
            HttpStatusCode.NoContent);
        Assert.Equal(
            otherMembership.Id,
            (await GetPersistedTaskAsync(ownTask.Id)).AssigneeMembershipId);
    }

    [Fact]
    public async Task LegalTaskRelatedResources_UnavailableIdentifiersUseGenericNonOracleResponses()
    {
        User actor = CreateUser("relations");
        User foreignUser = CreateUser("relations-foreign");
        User inactiveMembershipUser = CreateUser("relations-inactive-membership");
        User inactiveUser = CreateUser("relations-inactive-user");
        Organization organization = CreateOrganization("Relations");
        Organization foreignOrganization = CreateOrganization("Foreign Relations");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        OrganizationMembership foreignMembership = CreateMembership(
            foreignUser,
            foreignOrganization,
            OrganizationRole.Member);
        OrganizationMembership inactiveMembership = CreateMembership(
            inactiveMembershipUser,
            organization,
            OrganizationRole.Member);
        inactiveMembership.Deactivate();
        OrganizationMembership inactiveUserMembership = CreateMembership(
            inactiveUser,
            organization,
            OrganizationRole.Member);
        inactiveUser.Deactivate();
        ClientEntity foreignClient = CreateClient(foreignOrganization, "Foreign Client", 5);
        LegalProcess foreignProcess = CreateProcess(
            foreignOrganization,
            foreignClient,
            "Foreign Process",
            4);
        ClientEntity inactiveClient = CreateClient(
            organization,
            "Inactive Client",
            5);
        inactiveClient.Deactivate();
        LegalProcess inactiveClientProcess = CreateProcess(
            organization,
            inactiveClient,
            "Inactive Client Process",
            4);
        LegalTask legalTask = CreateTask(
            organization,
            actorMembership,
            "Relations task",
            createdMinutesAgo: 3);
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [foreignUser, inactiveMembershipUser, inactiveUser],
            [organization, foreignOrganization],
            [
                actorMembership,
                foreignMembership,
                inactiveMembership,
                inactiveUserMembership
            ],
            [foreignClient, inactiveClient],
            [foreignProcess, inactiveClientProcess],
            [legalTask]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        HttpStatusCode[] processStatuses = new HttpStatusCode[2];
        Guid[] processIds = [Guid.NewGuid(), foreignProcess.Id];
        for (int index = 0; index < processIds.Length; index++)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Post,
                GetTasksPath(organization.Id),
                rawHandle,
                csrf,
                new { title = "Unavailable process", processId = processIds[index] });
            processStatuses[index] = response.StatusCode;
            await AssertEmptyResponseAsync(response, HttpStatusCode.NotFound);
        }
        Assert.Equal(processStatuses[0], processStatuses[1]);

        (string? Title, string? Detail)[] assigneeResponses =
            new (string?, string?)[4];
        Guid[] assigneeIds =
        [
            Guid.NewGuid(),
            foreignMembership.Id,
            inactiveMembership.Id,
            inactiveUserMembership.Id
        ];
        for (int index = 0; index < assigneeIds.Length; index++)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Put,
                GetAssigneePath(organization.Id, legalTask.Id),
                rawHandle,
                csrf,
                new { assigneeMembershipId = assigneeIds[index] });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using JsonDocument responseDocument = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            assigneeResponses[index] = (
                responseDocument.RootElement.GetProperty("title").GetString(),
                responseDocument.RootElement.GetProperty("detail").GetString());
        }
        Assert.All(
            assigneeResponses,
            response => Assert.Equal(assigneeResponses[0], response));
        Assert.DoesNotContain(
            "inactive",
            assigneeResponses[0].Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "organization",
            assigneeResponses[0].Detail,
            StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage inactiveClientCreateResponse =
            await SendMutationAsync(
                HttpMethod.Post,
                GetTasksPath(organization.Id),
                rawHandle,
                csrf,
                new
                {
                    title = "Inactive client remains allowed",
                    processId = inactiveClientProcess.Id
                });
        Assert.Equal(HttpStatusCode.Created, inactiveClientCreateResponse.StatusCode);

        using HttpResponseMessage crossFilterResponse = await SendGetAsync(
            GetTasksPath(organization.Id) +
                $"?processId={foreignProcess.Id:D}&assignee={foreignMembership.Id:D}",
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, crossFilterResponse.StatusCode);
        ListLegalTasksResponse crossFilter = Assert.IsType<ListLegalTasksResponse>(
            await crossFilterResponse.Content
                .ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Empty(crossFilter.Items);

        using HttpResponseMessage missingProcessUpdateResponse =
            await SendMutationAsync(
                HttpMethod.Put,
                GetTaskPath(organization.Id, legalTask.Id),
                rawHandle,
                csrf,
                new { title = "Missing process", processId = Guid.NewGuid() });
        using HttpResponseMessage crossProcessUpdateResponse =
            await SendMutationAsync(
                HttpMethod.Put,
                GetTaskPath(organization.Id, legalTask.Id),
                rawHandle,
                csrf,
                new { title = "Cross process", processId = foreignProcess.Id });
        await AssertEmptyResponseAsync(
            missingProcessUpdateResponse,
            HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(
            crossProcessUpdateResponse,
            HttpStatusCode.NotFound);

        legalTask.Complete(Now);
        await SaveTaskCompletionAsync(legalTask.Id, Now);
        using HttpResponseMessage completedUpdateResponse =
            await SendMutationAsync(
                HttpMethod.Put,
                GetTaskPath(organization.Id, legalTask.Id),
                rawHandle,
                csrf,
                new { title = "Conflict first", processId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, completedUpdateResponse.StatusCode);
    }

    [Fact]
    public async Task LegalTaskHistoricalRead_InactiveRelatedIdentitiesAndClientRemainVisible()
    {
        User actor = CreateUser("historical-actor");
        User historical = CreateUser("historical-related");
        Organization organization = CreateOrganization("Historical");
        OrganizationMembership actorMembership = CreateMembership(
            actor,
            organization,
            OrganizationRole.Owner);
        OrganizationMembership historicalMembership = CreateMembership(
            historical,
            organization,
            OrganizationRole.Member);
        ClientEntity relatedClient = CreateClient(organization, "Historical Client", 5);
        LegalProcess process = CreateProcess(
            organization,
            relatedClient,
            "Historical Process",
            4);
        LegalTask legalTask = CreateTask(
            organization,
            historicalMembership,
            "Historical task",
            processId: process.Id,
            assigneeMembershipId: historicalMembership.Id,
            createdMinutesAgo: 3);
        historicalMembership.Deactivate();
        historical.Deactivate();
        relatedClient.Deactivate();
        string rawHandle = await SeedAuthenticatedUserAsync(
            actor,
            [historical],
            [organization],
            [actorMembership, historicalMembership],
            [relatedClient],
            [process],
            [legalTask]);

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetTaskPath(organization.Id, legalTask.Id),
            rawHandle);
        LegalTaskResponse detail = Assert.IsType<LegalTaskResponse>(
            await getResponse.Content.ReadFromJsonAsync<LegalTaskResponse>());
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(historical.Name, detail.CreatedByDisplayName);
        Assert.Equal(historical.Name, detail.AssigneeDisplayName);
        Assert.Equal(relatedClient.Name, detail.ClientName);
        Assert.Equal(process.Title, detail.ProcessTitle);

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetTasksPath(organization.Id),
            rawHandle);
        ListLegalTasksResponse list = Assert.IsType<ListLegalTasksResponse>(
            await listResponse.Content.ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Equal(legalTask.Id, Assert.Single(list.Items).Id);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties().Select(property => property.Name).ToArray();
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Task HTTP {marker}",
            $"task-http-{marker}-{Guid.NewGuid():N}@example.test",
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

    private static LegalTask CreateTask(
        Organization organization,
        OrganizationMembership creator,
        string title,
        string? description = null,
        DateOnly? dueDate = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null,
        int createdMinutesAgo = 1)
    {
        return new LegalTask(
            organization.Id,
            title,
            description,
            dueDate,
            processId,
            assigneeMembershipId,
            creator.Id,
            Now.AddMinutes(-createdMinutesAgo));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User actor,
        IReadOnlyCollection<User> otherUsers,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<ClientEntity> clients,
        IReadOnlyCollection<LegalProcess> processes,
        IReadOnlyCollection<LegalTask> tasks)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            actor.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            actor.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.Add(actor);
        dbContext.Users.AddRange(otherUsers);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.Clients.AddRange(clients);
        dbContext.LegalProcesses.AddRange(processes);
        dbContext.LegalTasks.AddRange(tasks);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse result = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendGetAsync(string path, string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
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
        AddCookiesAndCsrf(
            request,
            rawHandle,
            csrf,
            requestTokenOverride ?? csrf?.RequestToken);
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
        var cookies = new List<string> { $"{SessionCookieName}={rawHandle}" };
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
        return response.Headers.TryGetValues(
            HeaderNames.SetCookie,
            out IEnumerable<string>? values)
                ? SetCookieHeaderValue.ParseList(values.ToList()).ToArray()
                : [];
    }

    private async Task<LegalTask> GetPersistedTaskAsync(Guid taskId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == taskId);
    }

    private async Task<int> CountTasksAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks.CountAsync();
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

    private async Task SaveTaskCompletionAsync(
        Guid taskId,
        DateTimeOffset completedAt)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalTask task = await dbContext.LegalTasks
            .SingleAsync(candidate => candidate.Id == taskId);
        task.Complete(completedAt);
        await dbContext.SaveChangesAsync();
    }

    private async Task AssertListIdsAsync(
        string query,
        string rawHandle,
        Guid organizationId,
        params Guid[] expectedIds)
    {
        using HttpResponseMessage response = await SendGetAsync(
            GetTasksPath(organizationId) + query,
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListLegalTasksResponse result = Assert.IsType<ListLegalTasksResponse>(
            await response.Content.ReadFromJsonAsync<ListLegalTasksResponse>());
        Assert.Equal(expectedIds, result.Items.Select(item => item.Id).ToArray());
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

    private static string GetTasksPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/tasks";
    }

    private static string GetTaskPath(Guid organizationId, Guid taskId)
    {
        return $"{GetTasksPath(organizationId)}/{taskId:D}";
    }

    private static string GetAssigneePath(Guid organizationId, Guid taskId)
    {
        return $"{GetTaskPath(organizationId, taskId)}/assignee";
    }

    private static string GetCompletePath(Guid organizationId, Guid taskId)
    {
        return $"{GetTaskPath(organizationId, taskId)}/complete";
    }

    private static string GetReopenPath(Guid organizationId, Guid taskId)
    {
        return $"{GetTaskPath(organizationId, taskId)}/reopen";
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
