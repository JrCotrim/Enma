using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.CalendarEvents;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.CalendarEvents;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-calendar-event-http-password-hash";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public CalendarEventEndpointTests(PostgreSqlFixture fixture)
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

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public void CalendarEventContracts_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(CreateCalendarEventRequest.Title),
                nameof(CreateCalendarEventRequest.Description),
                nameof(CreateCalendarEventRequest.StartsAt),
                nameof(CreateCalendarEventRequest.EndsAt),
                nameof(CreateCalendarEventRequest.Location),
                nameof(CreateCalendarEventRequest.ClientId),
                nameof(CreateCalendarEventRequest.ProcessId),
                nameof(CreateCalendarEventRequest.AssigneeMembershipId)
            ],
            GetPropertyNames<CreateCalendarEventRequest>());
        Assert.Equal(
            [
                nameof(UpdateCalendarEventRequest.Title),
                nameof(UpdateCalendarEventRequest.Description),
                nameof(UpdateCalendarEventRequest.StartsAt),
                nameof(UpdateCalendarEventRequest.EndsAt),
                nameof(UpdateCalendarEventRequest.Location),
                nameof(UpdateCalendarEventRequest.ClientId),
                nameof(UpdateCalendarEventRequest.ProcessId)
            ],
            GetPropertyNames<UpdateCalendarEventRequest>());
        Assert.Equal(
            [nameof(ChangeCalendarEventAssigneeRequest.AssigneeMembershipId)],
            GetPropertyNames<ChangeCalendarEventAssigneeRequest>());
        Assert.Equal(
            [nameof(CreateCalendarEventResponse.Id)],
            GetPropertyNames<CreateCalendarEventResponse>());
        Assert.Equal(
            [
                nameof(CalendarEventResponse.Id),
                nameof(CalendarEventResponse.Title),
                nameof(CalendarEventResponse.Description),
                nameof(CalendarEventResponse.StartsAt),
                nameof(CalendarEventResponse.EndsAt),
                nameof(CalendarEventResponse.Location),
                nameof(CalendarEventResponse.ClientId),
                nameof(CalendarEventResponse.ClientName),
                nameof(CalendarEventResponse.ProcessId),
                nameof(CalendarEventResponse.ProcessTitle),
                nameof(CalendarEventResponse.AssigneeMembershipId),
                nameof(CalendarEventResponse.AssigneeDisplayName),
                nameof(CalendarEventResponse.CreatedByMembershipId),
                nameof(CalendarEventResponse.CreatedByDisplayName),
                nameof(CalendarEventResponse.CreatedAt)
            ],
            GetPropertyNames<CalendarEventResponse>());

        string[] forbiddenRequestNames =
        [
            "Id",
            "UserId",
            "OrganizationId",
            "CreatedByMembershipId",
            "CreatedAt",
            "Role",
            "OrganizationRole"
        ];
        foreach (Type requestType in new[]
        {
            typeof(CreateCalendarEventRequest),
            typeof(UpdateCalendarEventRequest),
            typeof(ChangeCalendarEventAssigneeRequest)
        })
        {
            Assert.DoesNotContain(
                requestType.GetProperties(),
                property => forbiddenRequestNames.Contains(property.Name));
        }

        Assert.Null(typeof(UpdateCalendarEventRequest).GetProperty(
            nameof(CreateCalendarEventRequest.AssigneeMembershipId)));
        Assert.Equal(
            typeof(Guid?),
            typeof(ChangeCalendarEventAssigneeRequest)
                .GetProperty(nameof(
                    ChangeCalendarEventAssigneeRequest.AssigneeMembershipId))
                ?.PropertyType);
    }

    [Fact]
    public async Task CalendarEventEndpoints_AnonymousRequests_ReturnNoStoreUnauthorized()
    {
        Guid organizationId = Guid.NewGuid();
        Guid calendarEventId = Guid.NewGuid();
        object body = CreateRequestBody();
        HttpResponseMessage[] responses =
        [
            await client.PostAsJsonAsync(GetEventsPath(organizationId), body),
            await client.GetAsync(GetEventPath(organizationId, calendarEventId)),
            await client.PutAsJsonAsync(
                GetEventPath(organizationId, calendarEventId),
                body),
            await client.PutAsJsonAsync(
                GetAssigneePath(organizationId, calendarEventId),
                new { assigneeMembershipId = (Guid?)null }),
            await client.DeleteAsync(GetEventPath(organizationId, calendarEventId))
        ];

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                await AssertEmptyResponseAsync(
                    response,
                    HttpStatusCode.Unauthorized);
            }
        }
    }

    [Fact]
    public async Task CalendarEventEndpoints_MissingOrganizationAccess_ReturnNoStoreForbiddenBeforeCsrf()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        Guid unavailableOrganizationId = Guid.NewGuid();
        Guid calendarEventId = Guid.NewGuid();
        object body = CreateRequestBody();
        HttpResponseMessage[] responses =
        [
            await SendGetAsync(
                GetEventPath(unavailableOrganizationId, calendarEventId),
                rawHandle),
            await SendMutationAsync(
                HttpMethod.Post,
                GetEventsPath(unavailableOrganizationId),
                rawHandle,
                csrf: null,
                body),
            await SendMutationAsync(
                HttpMethod.Put,
                GetEventPath(unavailableOrganizationId, calendarEventId),
                rawHandle,
                csrf: null,
                body),
            await SendMutationAsync(
                HttpMethod.Put,
                GetAssigneePath(unavailableOrganizationId, calendarEventId),
                rawHandle,
                csrf: null,
                new { assigneeMembershipId = (Guid?)null }),
            await SendMutationAsync(
                HttpMethod.Delete,
                GetEventPath(unavailableOrganizationId, calendarEventId),
                rawHandle,
                csrf: null)
        ];

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
            }
        }
    }

    [Fact]
    public async Task CreateAndGetCalendarEvent_UsesSessionAuthorityAndStableContracts()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        var startsAt = DateTimeOffset.Parse("2026-09-03T09:00:00-03:00");
        var endsAt = DateTimeOffset.Parse("2026-09-03T10:30:00-03:00");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id) +
                $"?userId={graph.OtherUser.Id:D}&role=owner");
        AddCookiesAndCsrf(request, rawHandle, csrf, csrf.RequestToken);
        request.Headers.Add("X-User-Id", graph.OtherUser.Id.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            title = "  General hearing  ",
            description = "  Court room  ",
            startsAt,
            endsAt,
            location = "  Room 4  ",
            clientId = (Guid?)null,
            processId = (Guid?)null,
            assigneeMembershipId = (Guid?)null,
            userId = graph.OtherUser.Id,
            organizationId = Guid.NewGuid(),
            createdByMembershipId = graph.OtherMembership.Id,
            createdAt = Now.AddYears(-1),
            role = "owner"
        });
        using HttpResponseMessage createResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(createResponse.Headers.CacheControl?.NoStore);
        CreateCalendarEventResponse created = Assert.IsType<
            CreateCalendarEventResponse>(
            await createResponse.Content
                .ReadFromJsonAsync<CreateCalendarEventResponse>());
        Assert.Equal(
            GetEventPath(graph.Organization.Id, created.Id),
            createResponse.Headers.Location?.OriginalString);

        CalendarEvent persisted = await GetPersistedEventAsync(created.Id);
        Assert.Equal(graph.Organization.Id, persisted.OrganizationId);
        Assert.Equal(graph.Membership.Id, persisted.CreatedByMembershipId);
        Assert.Equal("General hearing", persisted.Title);
        Assert.Equal("Court room", persisted.Description);
        Assert.Equal("Room 4", persisted.Location);
        Assert.Equal(startsAt.ToUniversalTime(), persisted.StartsAt);
        Assert.Equal(endsAt.ToUniversalTime(), persisted.EndsAt);
        Assert.Equal(Now, persisted.CreatedAt);

        using HttpResponseMessage privilegedAssignment =
            await SendMutationAsync(
                HttpMethod.Put,
                GetAssigneePath(graph.Organization.Id, created.Id),
                rawHandle,
                csrf,
                new { assigneeMembershipId = graph.OtherMembership.Id });
        await AssertEmptyResponseAsync(
            privilegedAssignment,
            HttpStatusCode.NoContent);
        Assert.Equal(
            graph.OtherMembership.Id,
            (await GetPersistedEventAsync(created.Id)).AssigneeMembershipId);

        using HttpResponseMessage clientCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Client meeting",
                clientId: graph.Client.Id));
        using HttpResponseMessage processCreateResponse = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Process hearing",
                processId: graph.Process.Id,
                assigneeMembershipId: graph.OtherMembership.Id));
        Assert.Equal(HttpStatusCode.Created, clientCreateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, processCreateResponse.StatusCode);
        CreateCalendarEventResponse processCreated = Assert.IsType<
            CreateCalendarEventResponse>(
            await processCreateResponse.Content
                .ReadFromJsonAsync<CreateCalendarEventResponse>());

        using HttpResponseMessage getResponse = await SendGetAsync(
            GetEventPath(graph.Organization.Id, processCreated.Id),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        CalendarEventResponse detail = Assert.IsType<CalendarEventResponse>(
            await getResponse.Content.ReadFromJsonAsync<CalendarEventResponse>());
        Assert.Equal(processCreated.Id, detail.Id);
        Assert.Equal(graph.Process.Id, detail.ProcessId);
        Assert.Equal(graph.Process.Title, detail.ProcessTitle);
        Assert.Null(detail.ClientId);
        Assert.Null(detail.ClientName);
        Assert.Equal(graph.OtherMembership.Id, detail.AssigneeMembershipId);
        Assert.Equal(graph.OtherUser.Name, detail.AssigneeDisplayName);
        Assert.Equal(graph.Membership.Id, detail.CreatedByMembershipId);
        Assert.Equal(graph.Actor.Name, detail.CreatedByDisplayName);
        Assert.Equal(Now, detail.CreatedAt);

        using JsonDocument detailJson = JsonDocument.Parse(
            await getResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            [
                "id",
                "title",
                "description",
                "startsAt",
                "endsAt",
                "location",
                "clientId",
                "clientName",
                "processId",
                "processTitle",
                "assigneeMembershipId",
                "assigneeDisplayName",
                "createdByMembershipId",
                "createdByDisplayName",
                "createdAt"
            ],
            detailJson.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task CalendarEventMutations_MissingCsrf_AllRoutesRejectWithoutMutation()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        CalendarEvent calendarEvent = CreateEvent(
            graph.Organization.Id,
            graph.Membership.Id,
            title: "Unchanged");
        graph.Events.Add(calendarEvent);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        int originalCount = await CountEventsAsync();

        HttpResponseMessage[] responses =
        [
            await SendMutationAsync(
                HttpMethod.Post,
                GetEventsPath(graph.Organization.Id),
                rawHandle,
                csrf: null,
                CreateRequestBody()),
            await SendMutationAsync(
                HttpMethod.Put,
                GetEventPath(graph.Organization.Id, calendarEvent.Id),
                rawHandle,
                csrf: null,
                CreateRequestBody("Changed")),
            await SendMutationAsync(
                HttpMethod.Put,
                GetAssigneePath(graph.Organization.Id, calendarEvent.Id),
                rawHandle,
                csrf: null,
                new { assigneeMembershipId = graph.Membership.Id }),
            await SendMutationAsync(
                HttpMethod.Delete,
                GetEventPath(graph.Organization.Id, calendarEvent.Id),
                rawHandle,
                csrf: null)
        ];

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
            }
        }

        CalendarEvent persisted = await GetPersistedEventAsync(calendarEvent.Id);
        Assert.Equal("Unchanged", persisted.Title);
        Assert.Null(persisted.AssigneeMembershipId);
        Assert.Equal(originalCount, await CountEventsAsync());
    }

    [Fact]
    public async Task MemberMutations_EnforceCreatorAndAssignmentRulesAndAllowExplicitNull()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Member);
        CalendarEvent ownEvent = CreateEvent(
            graph.Organization.Id,
            graph.Membership.Id,
            title: "Member event",
            assigneeMembershipId: graph.Membership.Id);
        CalendarEvent assignedButForeignAuthEvent = CreateEvent(
            graph.Organization.Id,
            graph.OtherMembership.Id,
            title: "Other creator",
            assigneeMembershipId: graph.Membership.Id);
        graph.Events.AddRange([ownEvent, assignedButForeignAuthEvent]);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage createNull = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody("Member unassigned"));
        using HttpResponseMessage createSelf = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Member self",
                assigneeMembershipId: graph.Membership.Id));
        using HttpResponseMessage createOther = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Member other",
                assigneeMembershipId: graph.OtherMembership.Id));
        Assert.Equal(HttpStatusCode.Created, createNull.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createSelf.StatusCode);
        await AssertEmptyResponseAsync(createOther, HttpStatusCode.Forbidden);

        using HttpResponseMessage update = await SendMutationAsync(
            HttpMethod.Put,
            GetEventPath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            new
            {
                title = "Updated by creator",
                description = "Updated",
                startsAt = DateTimeOffset.Parse("2026-09-04T10:00:00-03:00"),
                endsAt = DateTimeOffset.Parse("2026-09-04T11:00:00-03:00"),
                location = "Office",
                clientId = (Guid?)null,
                processId = (Guid?)null,
                assigneeMembershipId = graph.OtherMembership.Id,
                createdByMembershipId = graph.OtherMembership.Id,
                createdAt = Now.AddYears(-10)
            });
        await AssertEmptyResponseAsync(update, HttpStatusCode.NoContent);
        CalendarEvent updated = await GetPersistedEventAsync(ownEvent.Id);
        Assert.Equal("Updated by creator", updated.Title);
        Assert.Equal(graph.Membership.Id, updated.CreatedByMembershipId);
        Assert.Equal(graph.Membership.Id, updated.AssigneeMembershipId);

        using HttpResponseMessage clear = await SendRawJsonAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            "{\"assigneeMembershipId\":null}");
        await AssertEmptyResponseAsync(clear, HttpStatusCode.NoContent);
        Assert.Null((await GetPersistedEventAsync(ownEvent.Id)).AssigneeMembershipId);

        using HttpResponseMessage self = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = graph.Membership.Id });
        await AssertEmptyResponseAsync(self, HttpStatusCode.NoContent);

        using HttpResponseMessage omitted = await SendRawJsonAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            "{}");
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        Assert.True(omitted.Headers.CacheControl?.NoStore);

        using HttpResponseMessage assignOther = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = graph.OtherMembership.Id });
        await AssertEmptyResponseAsync(assignOther, HttpStatusCode.Forbidden);

        using HttpResponseMessage updateOther = await SendMutationAsync(
            HttpMethod.Put,
            GetEventPath(graph.Organization.Id, assignedButForeignAuthEvent.Id),
            rawHandle,
            csrf,
            CreateRequestBody("Unauthorized update"));
        using HttpResponseMessage assignOtherEvent = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(
                graph.Organization.Id,
                assignedButForeignAuthEvent.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = graph.Membership.Id });
        using HttpResponseMessage deleteOther = await SendMutationAsync(
            HttpMethod.Delete,
            GetEventPath(graph.Organization.Id, assignedButForeignAuthEvent.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(updateOther, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(assignOtherEvent, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(deleteOther, HttpStatusCode.Forbidden);
        Assert.Equal(
            "Other creator",
            (await GetPersistedEventAsync(assignedButForeignAuthEvent.Id)).Title);

        using HttpResponseMessage deleteOwn = await SendMutationAsync(
            HttpMethod.Delete,
            GetEventPath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(deleteOwn, HttpStatusCode.NoContent);
        Assert.False(await EventExistsAsync(ownEvent.Id));
    }

    [Fact]
    public async Task CalendarEvent_InvalidInputAndStructuralJson_ReturnNoStoreBadRequest()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        HttpResponseMessage[] responses =
        [
            await SendMutationAsync(
                HttpMethod.Post,
                GetEventsPath(graph.Organization.Id),
                rawHandle,
                csrf,
                CreateRequestBody(
                    clientId: graph.Client.Id,
                    processId: graph.Process.Id)),
            await SendRawJsonAsync(
                HttpMethod.Post,
                GetEventsPath(graph.Organization.Id),
                rawHandle,
                csrf,
                "{\"title\":\"Missing dates\"}"),
            await SendRawJsonAsync(
                HttpMethod.Post,
                GetEventsPath(graph.Organization.Id),
                rawHandle,
                csrf,
                "{\"title\":\"Malformed\",\"startsAt\":\"not-a-date\",\"endsAt\":\"also-invalid\"}"),
            await SendMutationAsync(
                HttpMethod.Post,
                GetEventsPath(graph.Organization.Id),
                rawHandle,
                csrf,
                new
                {
                    title = "Invalid range",
                    startsAt = DateTimeOffset.Parse("2026-09-03T11:00:00-03:00"),
                    endsAt = DateTimeOffset.Parse("2026-09-03T10:00:00-03:00")
                })
        ];

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task CalendarEvent_ForeignAndMissingResources_AreIndistinguishable()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        Organization foreignOrganization = CreateOrganization("Foreign calendar");
        User foreignUser = CreateUser("foreign-calendar");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Owner,
            Now);
        var foreignClient = new ClientEntity(
            foreignOrganization.Id,
            "Foreign client",
            Now);
        var foreignProcess = new LegalProcess(
            foreignOrganization.Id,
            foreignClient.Id,
            "Foreign process",
            Now);
        CalendarEvent foreignEvent = CreateEvent(
            foreignOrganization.Id,
            foreignMembership.Id,
            title: "Foreign event");
        graph.Organizations.Add(foreignOrganization);
        graph.Users.Add(foreignUser);
        graph.Memberships.Add(foreignMembership);
        graph.Clients.Add(foreignClient);
        graph.Processes.Add(foreignProcess);
        graph.Events.Add(foreignEvent);
        CalendarEvent ownEvent = CreateEvent(
            graph.Organization.Id,
            graph.Membership.Id,
            title: "Own unchanged");
        graph.Events.Add(ownEvent);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Guid missingId = Guid.NewGuid();

        using HttpResponseMessage foreignGet = await SendGetAsync(
            GetEventPath(graph.Organization.Id, foreignEvent.Id),
            rawHandle);
        using HttpResponseMessage missingGet = await SendGetAsync(
            GetEventPath(graph.Organization.Id, missingId),
            rawHandle);
        await AssertEquivalentEmptyResponsesAsync(
            foreignGet,
            missingGet,
            HttpStatusCode.NotFound);

        (HttpMethod Method, Func<Guid, string> Path, object? Body)[] mutations =
        [
            (
                HttpMethod.Put,
                (Guid id) => GetEventPath(graph.Organization.Id, id),
                CreateRequestBody("Attempted update")),
            (
                HttpMethod.Put,
                (Guid id) => GetAssigneePath(graph.Organization.Id, id),
                (object)new { assigneeMembershipId = graph.Membership.Id }),
            (
                HttpMethod.Delete,
                (Guid id) => GetEventPath(graph.Organization.Id, id),
                (object?)null)
        ];

        foreach ((HttpMethod method, Func<Guid, string> path, object? body) in mutations)
        {
            using HttpResponseMessage foreign = await SendMutationAsync(
                method,
                path(foreignEvent.Id),
                rawHandle,
                csrf,
                body);
            using HttpResponseMessage missing = await SendMutationAsync(
                method,
                path(missingId),
                rawHandle,
                csrf,
                body);
            await AssertEquivalentEmptyResponsesAsync(
                foreign,
                missing,
                HttpStatusCode.NotFound);
        }

        Assert.True(await EventExistsAsync(foreignEvent.Id));
        Assert.Equal("Foreign event", (await GetPersistedEventAsync(foreignEvent.Id)).Title);
        Assert.Equal("Own unchanged", (await GetPersistedEventAsync(ownEvent.Id)).Title);
    }

    [Fact]
    public async Task CalendarEvent_RelatedResources_MapMissingAndForeignIdentically()
    {
        CalendarGraph graph = CreateGraph(OrganizationRole.Owner);
        Organization foreignOrganization = CreateOrganization("Foreign related");
        User foreignUser = CreateUser("foreign-related");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            Now);
        var foreignClient = new ClientEntity(
            foreignOrganization.Id,
            "Foreign related client",
            Now);
        var foreignProcess = new LegalProcess(
            foreignOrganization.Id,
            foreignClient.Id,
            "Foreign related process",
            Now);
        CalendarEvent ownEvent = CreateEvent(
            graph.Organization.Id,
            graph.Membership.Id,
            title: "Related unchanged");
        graph.Organizations.Add(foreignOrganization);
        graph.Users.Add(foreignUser);
        graph.Memberships.Add(foreignMembership);
        graph.Clients.Add(foreignClient);
        graph.Processes.Add(foreignProcess);
        graph.Events.Add(ownEvent);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Guid missingId = Guid.NewGuid();

        await AssertRelatedNotFoundPairAsync(
            CreateRequestBody("Missing client", clientId: missingId),
            CreateRequestBody("Foreign client", clientId: foreignClient.Id),
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf);
        await AssertRelatedNotFoundPairAsync(
            CreateRequestBody("Missing process", processId: missingId),
            CreateRequestBody("Foreign process", processId: foreignProcess.Id),
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf);

        using HttpResponseMessage missingAssigneeCreate = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Missing assignee",
                assigneeMembershipId: missingId));
        using HttpResponseMessage foreignAssigneeCreate = await SendMutationAsync(
            HttpMethod.Post,
            GetEventsPath(graph.Organization.Id),
            rawHandle,
            csrf,
            CreateRequestBody(
                "Foreign assignee",
                assigneeMembershipId: foreignMembership.Id));
        await AssertEquivalentProblemsAsync(
            missingAssigneeCreate,
            foreignAssigneeCreate,
            HttpStatusCode.BadRequest,
            "Related assignee unavailable");

        string updatePath = GetEventPath(graph.Organization.Id, ownEvent.Id);
        await AssertRelatedNotFoundPairAsync(
            CreateRequestBody("Missing client update", clientId: missingId),
            CreateRequestBody(
                "Foreign client update",
                clientId: foreignClient.Id),
            updatePath,
            rawHandle,
            csrf,
            HttpMethod.Put);
        await AssertRelatedNotFoundPairAsync(
            CreateRequestBody("Missing process update", processId: missingId),
            CreateRequestBody(
                "Foreign process update",
                processId: foreignProcess.Id),
            updatePath,
            rawHandle,
            csrf,
            HttpMethod.Put);

        using HttpResponseMessage missingAssignee = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = missingId });
        using HttpResponseMessage foreignAssignee = await SendMutationAsync(
            HttpMethod.Put,
            GetAssigneePath(graph.Organization.Id, ownEvent.Id),
            rawHandle,
            csrf,
            new { assigneeMembershipId = foreignMembership.Id });
        await AssertEquivalentProblemsAsync(
            missingAssignee,
            foreignAssignee,
            HttpStatusCode.BadRequest,
            "Related assignee unavailable");

        CalendarEvent persisted = await GetPersistedEventAsync(ownEvent.Id);
        Assert.Equal("Related unchanged", persisted.Title);
        Assert.Null(persisted.ClientId);
        Assert.Null(persisted.ProcessId);
        Assert.Null(persisted.AssigneeMembershipId);
    }

    private async Task AssertRelatedNotFoundPairAsync(
        object missingBody,
        object foreignBody,
        string path,
        string rawHandle,
        CsrfPair csrf,
        HttpMethod? method = null)
    {
        using HttpResponseMessage missing = await SendMutationAsync(
            method ?? HttpMethod.Post,
            path,
            rawHandle,
            csrf,
            missingBody);
        using HttpResponseMessage foreign = await SendMutationAsync(
            method ?? HttpMethod.Post,
            path,
            rawHandle,
            csrf,
            foreignBody);
        await AssertEquivalentEmptyResponsesAsync(
            missing,
            foreign,
            HttpStatusCode.NotFound);
    }

    private static async Task AssertEquivalentProblemsAsync(
        HttpResponseMessage first,
        HttpResponseMessage second,
        HttpStatusCode expectedStatusCode,
        string expectedTitle)
    {
        Assert.Equal(expectedStatusCode, first.StatusCode);
        Assert.Equal(expectedStatusCode, second.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.True(second.Headers.CacheControl?.NoStore);
        ProblemDetails firstProblem = Assert.IsType<ProblemDetails>(
            await first.Content.ReadFromJsonAsync<ProblemDetails>());
        ProblemDetails secondProblem = Assert.IsType<ProblemDetails>(
            await second.Content.ReadFromJsonAsync<ProblemDetails>());
        Assert.Equal(expectedTitle, firstProblem.Title);
        Assert.Equal(firstProblem.Title, secondProblem.Title);
        Assert.Equal(firstProblem.Detail, secondProblem.Detail);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties()
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToArray();
    }

    private static object CreateRequestBody(
        string title = "Calendar event",
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new
        {
            title,
            description = "Description",
            startsAt = DateTimeOffset.Parse("2026-09-03T09:00:00-03:00"),
            endsAt = DateTimeOffset.Parse("2026-09-03T10:00:00-03:00"),
            location = "Location",
            clientId,
            processId,
            assigneeMembershipId
        };
    }

    private static CalendarGraph CreateGraph(OrganizationRole role)
    {
        Organization organization = CreateOrganization("Calendar HTTP");
        User actor = CreateUser("calendar-actor");
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            role,
            Now);
        User otherUser = CreateUser("calendar-other");
        var otherMembership = new OrganizationMembership(
            organization.Id,
            otherUser.Id,
            OrganizationRole.Member,
            Now);
        var legalClient = new ClientEntity(
            organization.Id,
            "Calendar client",
            Now);
        var legalProcess = new LegalProcess(
            organization.Id,
            legalClient.Id,
            "Calendar process",
            Now);

        return new CalendarGraph(
            organization,
            actor,
            membership,
            otherUser,
            otherMembership,
            legalClient,
            legalProcess,
            [organization],
            [actor, otherUser],
            [membership, otherMembership],
            [legalClient],
            [legalProcess],
            []);
    }

    private static Organization CreateOrganization(string name)
    {
        string marker = Guid.NewGuid().ToString("N");
        return new Organization(name, $"calendar-http-{marker}", Now);
    }

    private static User CreateUser(string marker)
    {
        string unique = Guid.NewGuid().ToString("N");
        return new User(
            $"Calendar {marker}",
            $"calendar-{marker}-{unique}@example.test",
            Now);
    }

    private static CalendarEvent CreateEvent(
        Guid organizationId,
        Guid creatorMembershipId,
        string title = "Calendar event",
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new CalendarEvent(
            organizationId,
            title,
            "Description",
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"),
            "Location",
            clientId,
            processId,
            assigneeMembershipId,
            creatorMembershipId,
            Now);
    }

    private async Task<string> SeedAuthenticatedAsync(CalendarGraph graph)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            graph.Actor.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            graph.Actor.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(graph.Organizations);
        dbContext.Users.AddRange(graph.Users);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(graph.Memberships);
        dbContext.Clients.AddRange(graph.Clients);
        dbContext.LegalProcesses.AddRange(graph.Processes);
        dbContext.CalendarEvents.AddRange(graph.Events);
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
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        AddCookiesAndCsrf(request, rawHandle, csrf, csrf?.RequestToken);
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

    private async Task<CalendarEvent> GetPersistedEventAsync(Guid id)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id);
    }

    private async Task<bool> EventExistsAsync(Guid id)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents.AnyAsync(candidate => candidate.Id == id);
    }

    private async Task<int> CountEventsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents.CountAsync();
    }

    private static async Task AssertEquivalentEmptyResponsesAsync(
        HttpResponseMessage first,
        HttpResponseMessage second,
        HttpStatusCode expectedStatusCode)
    {
        await AssertEmptyResponseAsync(first, expectedStatusCode);
        await AssertEmptyResponseAsync(second, expectedStatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
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

    private static string GetEventsPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/calendar-events";
    }

    private static string GetEventPath(
        Guid organizationId,
        Guid calendarEventId)
    {
        return $"{GetEventsPath(organizationId)}/{calendarEventId:D}";
    }

    private static string GetAssigneePath(
        Guid organizationId,
        Guid calendarEventId)
    {
        return $"{GetEventPath(organizationId, calendarEventId)}/assignee";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);

    private sealed record CalendarGraph(
        Organization Organization,
        User Actor,
        OrganizationMembership Membership,
        User OtherUser,
        OrganizationMembership OtherMembership,
        ClientEntity Client,
        LegalProcess Process,
        List<Organization> Organizations,
        List<User> Users,
        List<OrganizationMembership> Memberships,
        List<ClientEntity> Clients,
        List<LegalProcess> Processes,
        List<CalendarEvent> Events);
}
