using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Enma.Api.Contracts.Agenda;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using ClientEntity = Enma.Domain.Clients.Client;

namespace Enma.IntegrationTests.Api.Agenda;

[Collection(PostgreSqlCollection.Name)]
public sealed class AgendaEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-agenda-http-password-hash";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public AgendaEndpointTests(PostgreSqlFixture fixture)
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
    public void AgendaContracts_ExposeOnlyApprovedFieldsAndPublicEnum()
    {
        Assert.Equal(
            [nameof(GetAgendaResponse.Items)],
            GetPropertyNames<GetAgendaResponse>());
        Assert.Equal(
            [
                nameof(AgendaItemResponse.Kind),
                nameof(AgendaItemResponse.Id),
                nameof(AgendaItemResponse.Title),
                nameof(AgendaItemResponse.IsAllDay),
                nameof(AgendaItemResponse.Date),
                nameof(AgendaItemResponse.StartsAt),
                nameof(AgendaItemResponse.EndsAt),
                nameof(AgendaItemResponse.CompletedAt),
                nameof(AgendaItemResponse.ClientId),
                nameof(AgendaItemResponse.ClientName),
                nameof(AgendaItemResponse.ProcessId),
                nameof(AgendaItemResponse.ProcessTitle),
                nameof(AgendaItemResponse.AssigneeMembershipId),
                nameof(AgendaItemResponse.AssigneeDisplayName)
            ],
            GetPropertyNames<AgendaItemResponse>());

        Assert.Equal(
            typeof(DateOnly?),
            typeof(AgendaItemResponse)
                .GetProperty(nameof(AgendaItemResponse.Date))?.PropertyType);
        Assert.Equal(
            typeof(DateTimeOffset?),
            typeof(AgendaItemResponse)
                .GetProperty(nameof(AgendaItemResponse.StartsAt))?.PropertyType);
        Assert.Equal(
            ["deadline", "task", "calendarEvent"],
            Enum.GetValues<AgendaItemKindResponse>()
                .Select(value => JsonSerializer.Serialize(value).Trim('"')));
        Assert.DoesNotContain(
            typeof(AgendaItemResponse).GetProperties(),
            property => property.Name == "OrganizationId");
    }

    [Fact]
    public async Task GetAgenda_AnonymousRequest_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetAgendaPath(
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-01T00:00:00-03:00"),
                DateTimeOffset.Parse("2026-09-08T00:00:00-03:00")));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAgenda_MissingOrganizationAccess_ReturnsNoStoreForbidden()
    {
        AgendaGraph graph = CreateGraph(OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetAgendaPath(
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-01T00:00:00-03:00"),
                DateTimeOffset.Parse("2026-09-08T00:00:00-03:00")),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task GetAgenda_LiveRole_ReturnsMixedPrivateContractAndPreservesOffsets(
        OrganizationRole role)
    {
        AgendaGraph graph = CreateGraph(role);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00-03:00");
        var to = DateTimeOffset.Parse("2026-09-08T00:00:00-03:00");

        using HttpResponseMessage response = await SendGetAsync(
            GetAgendaPath(graph.Organization.Id, from, to),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string json = await response.Content.ReadAsStringAsync();
        GetAgendaResponse result = Assert.IsType<GetAgendaResponse>(
            JsonSerializer.Deserialize<GetAgendaResponse>(
                json,
                JsonSerializerOptions.Web));

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(
            [
                AgendaItemKindResponse.Deadline,
                AgendaItemKindResponse.Task,
                AgendaItemKindResponse.CalendarEvent
            ],
            result.Items.Select(item => item.Kind));

        AgendaItemResponse deadline = result.Items[0];
        Assert.Equal(graph.Deadline.Id, deadline.Id);
        Assert.True(deadline.IsAllDay);
        Assert.Equal(new DateOnly(2026, 9, 1), deadline.Date);
        Assert.Null(deadline.StartsAt);
        Assert.Null(deadline.EndsAt);
        Assert.Equal(graph.Client.Id, deadline.ClientId);
        Assert.Equal(graph.Process.Id, deadline.ProcessId);

        AgendaItemResponse task = result.Items[1];
        Assert.Equal(graph.Task.Id, task.Id);
        Assert.True(task.IsAllDay);
        Assert.Equal(new DateOnly(2026, 9, 2), task.Date);
        Assert.Equal(Now.AddHours(1), task.CompletedAt);
        Assert.Equal(graph.Membership.Id, task.AssigneeMembershipId);
        Assert.Equal(graph.User.Name, task.AssigneeDisplayName);

        AgendaItemResponse calendarEvent = result.Items[2];
        Assert.Equal(graph.CalendarEvent.Id, calendarEvent.Id);
        Assert.False(calendarEvent.IsAllDay);
        Assert.Null(calendarEvent.Date);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T12:00:00Z"), calendarEvent.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T13:00:00Z"), calendarEvent.EndsAt);
        Assert.Null(calendarEvent.CompletedAt);
        Assert.Equal(graph.Process.Title, calendarEvent.ProcessTitle);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] items = document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Equal("deadline", items[0].GetProperty("kind").GetString());
        Assert.Equal("2026-09-01", items[0].GetProperty("date").GetString());
        Assert.DoesNotContain('T', items[0].GetProperty("date").GetString()!);
        Assert.Equal("task", items[1].GetProperty("kind").GetString());
        Assert.Equal("calendarEvent", items[2].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("date").ValueKind);
        Assert.Equal(JsonValueKind.String, items[2].GetProperty("startsAt").ValueKind);
        Assert.All(
            items,
            item => Assert.False(item.TryGetProperty("organizationId", out _)));

        var differingOffsetTo = DateTimeOffset.Parse(
            "2026-09-08T00:00:00-02:00");
        using HttpResponseMessage differingOffsetResponse = await SendGetAsync(
            GetAgendaPath(graph.Organization.Id, from, differingOffsetTo),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, differingOffsetResponse.StatusCode);
        GetAgendaResponse differingOffsetResult = Assert.IsType<GetAgendaResponse>(
            await differingOffsetResponse.Content
                .ReadFromJsonAsync<GetAgendaResponse>());
        Assert.Equal(
            result.Items.Select(item => item.Id),
            differingOffsetResult.Items.Select(item => item.Id));

        var emptyFrom = DateTimeOffset.Parse("2026-10-01T00:00:00-03:00");
        var emptyTo = DateTimeOffset.Parse("2026-10-02T00:00:00-03:00");
        using HttpResponseMessage emptyResponse = await SendGetAsync(
            GetAgendaPath(graph.Organization.Id, emptyFrom, emptyTo),
            rawHandle);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        GetAgendaResponse empty = Assert.IsType<GetAgendaResponse>(
            await emptyResponse.Content.ReadFromJsonAsync<GetAgendaResponse>());
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task GetAgenda_InvalidAndMalformedViewports_ReturnNoStoreBadRequest()
    {
        AgendaGraph graph = CreateGraph(OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedAsync(graph);
        string prefix = $"/api/organizations/{graph.Organization.Id:D}/agenda";
        string[] queries =
        [
            "?from=not-a-date&to=2026-09-08T00%3A00%3A00-03%3A00",
            "?from=2026-09-01T00%3A00%3A00-03%3A00&to=not-a-date",
            "?from=2026-09-01T12%3A00%3A00-03%3A00&to=2026-09-08T00%3A00%3A00-03%3A00",
            "?from=2026-09-01T00%3A00%3A00-03%3A00&to=2026-09-01T00%3A00%3A00-03%3A00",
            "?from=2026-01-01T00%3A00%3A00-03%3A00&to=2026-04-05T00%3A00%3A00-03%3A00"
        ];

        foreach (string query in queries)
        {
            using HttpResponseMessage response = await SendGetAsync(
                prefix + query,
                rawHandle);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task GetAgenda_CrossTenantItems_AreAbsent()
    {
        AgendaGraph graph = CreateGraph(OrganizationRole.Owner);
        Organization foreignOrganization = CreateOrganization("Foreign agenda");
        User foreignUser = CreateUser("foreign-agenda");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Owner,
            Now);
        CalendarEvent foreignEvent = CreateEvent(
            foreignOrganization.Id,
            foreignMembership.Id,
            title: "Foreign event");
        graph.Entities.AddRange(
            [foreignOrganization, foreignUser, foreignMembership, foreignEvent]);
        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetAgendaPath(
                graph.Organization.Id,
                DateTimeOffset.Parse("2026-09-01T00:00:00-03:00"),
                DateTimeOffset.Parse("2026-09-08T00:00:00-03:00")),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetAgendaResponse result = Assert.IsType<GetAgendaResponse>(
            await response.Content.ReadFromJsonAsync<GetAgendaResponse>());
        Assert.DoesNotContain(result.Items, item => item.Id == foreignEvent.Id);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties()
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToArray();
    }

    private static AgendaGraph CreateGraph(OrganizationRole role)
    {
        Organization organization = CreateOrganization("Agenda HTTP");
        User user = CreateUser("agenda-http");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now);
        var legalClient = new ClientEntity(
            organization.Id,
            "Agenda client",
            Now);
        var legalProcess = new LegalProcess(
            organization.Id,
            legalClient.Id,
            "Agenda process",
            Now);
        var deadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "Agenda deadline",
            new DateOnly(2026, 9, 1),
            Now);
        var legalTask = new LegalTask(
            organization.Id,
            "Agenda task",
            null,
            new DateOnly(2026, 9, 2),
            legalProcess.Id,
            membership.Id,
            membership.Id,
            Now);
        legalTask.Complete(Now.AddHours(1));
        CalendarEvent calendarEvent = CreateEvent(
            organization.Id,
            membership.Id,
            processId: legalProcess.Id);

        return new AgendaGraph(
            organization,
            user,
            membership,
            legalClient,
            legalProcess,
            deadline,
            legalTask,
            calendarEvent,
            [
                organization,
                user,
                membership,
                legalClient,
                legalProcess,
                deadline,
                legalTask,
                calendarEvent
            ]);
    }

    private static CalendarEvent CreateEvent(
        Guid organizationId,
        Guid creatorMembershipId,
        string title = "Agenda event",
        Guid? processId = null)
    {
        return new CalendarEvent(
            organizationId,
            title,
            null,
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"),
            null,
            null,
            processId,
            null,
            creatorMembershipId,
            Now);
    }

    private static Organization CreateOrganization(string name)
    {
        string marker = Guid.NewGuid().ToString("N");
        return new Organization(name, $"agenda-http-{marker}", Now);
    }

    private static User CreateUser(string marker)
    {
        string unique = Guid.NewGuid().ToString("N");
        return new User(
            $"Agenda {marker}",
            $"agenda-{marker}-{unique}@example.test",
            Now);
    }

    private async Task<string> SeedAuthenticatedAsync(AgendaGraph graph)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            graph.User.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            graph.User.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(graph.Entities);
        dbContext.UserCredentials.Add(credential);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();
        return rawHandle;
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

    private static string GetAgendaPath(
        Guid organizationId,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        return $"/api/organizations/{organizationId:D}/agenda" +
            $"?from={Uri.EscapeDataString(from.ToString("O"))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O"))}";
    }

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record AgendaGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        ClientEntity Client,
        LegalProcess Process,
        LegalDeadline Deadline,
        LegalTask Task,
        CalendarEvent CalendarEvent,
        List<object> Entities);
}
