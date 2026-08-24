using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Enma.Api.Contracts.Dashboard;
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

namespace Enma.IntegrationTests.Api.Dashboard;

[Collection(PostgreSqlCollection.Name)]
public sealed class DashboardEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-dashboard-http-password-hash";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T12:00:00Z");
    private static readonly DateOnly ReferenceDate = new(2026, 8, 24);
    private static readonly DateOnly ThroughDate = new(2026, 8, 31);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public DashboardEndpointTests(PostgreSqlFixture fixture)
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
    public void DashboardContracts_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(GetDashboardResponse.ReferenceDate),
                nameof(GetDashboardResponse.Summary),
                nameof(GetDashboardResponse.Attention),
                nameof(GetDashboardResponse.Upcoming)
            ],
            GetPropertyNames<GetDashboardResponse>());
        Assert.Equal(
            [
                nameof(DashboardSummaryResponse.ActiveClients),
                nameof(DashboardSummaryResponse.TotalLegalProcesses),
                nameof(DashboardSummaryResponse.PendingDeadlines),
                nameof(DashboardSummaryResponse.PendingTasks)
            ],
            GetPropertyNames<DashboardSummaryResponse>());
        Assert.Equal(
            [
                nameof(DashboardAttentionResponse.Deadlines),
                nameof(DashboardAttentionResponse.Tasks)
            ],
            GetPropertyNames<DashboardAttentionResponse>());
        Assert.Equal(
            [
                nameof(DashboardAttentionBucketResponse.Overdue),
                nameof(DashboardAttentionBucketResponse.DueToday),
                nameof(DashboardAttentionBucketResponse.DueInNextSevenDays)
            ],
            GetPropertyNames<DashboardAttentionBucketResponse>());
        Assert.Equal(
            [
                nameof(DashboardUpcomingResponse.ThroughDate),
                nameof(DashboardUpcomingResponse.Deadlines),
                nameof(DashboardUpcomingResponse.Tasks),
                nameof(DashboardUpcomingResponse.CalendarEvents)
            ],
            GetPropertyNames<DashboardUpcomingResponse>());
        Assert.Equal(
            [
                nameof(DashboardUpcomingDeadlineResponse.Id),
                nameof(DashboardUpcomingDeadlineResponse.Title),
                nameof(DashboardUpcomingDeadlineResponse.DueDate),
                nameof(DashboardUpcomingDeadlineResponse.ClientName),
                nameof(DashboardUpcomingDeadlineResponse.ProcessTitle)
            ],
            GetPropertyNames<DashboardUpcomingDeadlineResponse>());
        Assert.Equal(
            [
                nameof(DashboardUpcomingTaskResponse.Id),
                nameof(DashboardUpcomingTaskResponse.Title),
                nameof(DashboardUpcomingTaskResponse.DueDate),
                nameof(DashboardUpcomingTaskResponse.ClientName),
                nameof(DashboardUpcomingTaskResponse.ProcessTitle),
                nameof(DashboardUpcomingTaskResponse.AssigneeDisplayName)
            ],
            GetPropertyNames<DashboardUpcomingTaskResponse>());
        Assert.Equal(
            [
                nameof(DashboardUpcomingCalendarEventResponse.Id),
                nameof(DashboardUpcomingCalendarEventResponse.Title),
                nameof(DashboardUpcomingCalendarEventResponse.StartsAt),
                nameof(DashboardUpcomingCalendarEventResponse.EndsAt),
                nameof(DashboardUpcomingCalendarEventResponse.ClientName),
                nameof(DashboardUpcomingCalendarEventResponse.ProcessTitle),
                nameof(DashboardUpcomingCalendarEventResponse.AssigneeDisplayName)
            ],
            GetPropertyNames<DashboardUpcomingCalendarEventResponse>());
    }

    [Fact]
    public async Task GetDashboard_AnonymousRequest_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetDashboardPath(Guid.NewGuid()));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task GetDashboard_LiveRole_ReturnsTenantOperationalOverview(
        OrganizationRole role)
    {
        DashboardGraph own = CreateGraph(role, "own-dashboard-http");
        DashboardGraph foreign = CreateGraph(
            OrganizationRole.Owner,
            "foreign-dashboard-http");
        own.Entities.AddRange(foreign.Entities);
        string rawHandle = await SeedAuthenticatedAsync(own);

        using HttpResponseMessage response = await SendGetAsync(
            GetDashboardPath(own.Organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string json = await response.Content.ReadAsStringAsync();
        GetDashboardResponse result = Assert.IsType<GetDashboardResponse>(
            JsonSerializer.Deserialize<GetDashboardResponse>(
                json,
                JsonSerializerOptions.Web));
        Assert.Equal(ReferenceDate, result.ReferenceDate);
        Assert.Equal(1, result.Summary.ActiveClients);
        Assert.Equal(1, result.Summary.TotalLegalProcesses);
        Assert.Equal(1, result.Summary.PendingDeadlines);
        Assert.Equal(1, result.Summary.PendingTasks);
        Assert.Equal(
            new DashboardAttentionBucketResponse(0, 1, 0),
            result.Attention.Deadlines);
        Assert.Equal(
            new DashboardAttentionBucketResponse(0, 0, 1),
            result.Attention.Tasks);
        Assert.Equal(ThroughDate, result.Upcoming.ThroughDate);
        DashboardUpcomingDeadlineResponse deadline = Assert.Single(
            result.Upcoming.Deadlines);
        Assert.Equal(own.Deadline.Id, deadline.Id);
        Assert.Equal(own.Client.Name, deadline.ClientName);
        Assert.Equal(own.Process.Title, deadline.ProcessTitle);
        DashboardUpcomingTaskResponse task = Assert.Single(
            result.Upcoming.Tasks);
        Assert.Equal(own.Task.Id, task.Id);
        Assert.Equal(own.User.Name, task.AssigneeDisplayName);
        DashboardUpcomingCalendarEventResponse calendarEvent = Assert.Single(
            result.Upcoming.CalendarEvents);
        Assert.Equal(own.CalendarEvent.Id, calendarEvent.Id);
        Assert.Equal(own.Client.Name, calendarEvent.ClientName);
        Assert.Equal(own.Process.Title, calendarEvent.ProcessTitle);

        using JsonDocument document = JsonDocument.Parse(json);
        AssertNoUnwantedFields(document.RootElement);
        Assert.Equal(
            "2026-08-24",
            document.RootElement.GetProperty("referenceDate").GetString());
        Assert.Equal(
            "2026-08-31",
            document.RootElement
                .GetProperty("upcoming")
                .GetProperty("throughDate")
                .GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task GetDashboard_MissingOrInactiveMembership_ReturnsNoStoreForbidden(
        bool includeMembership,
        bool deactivateMembership)
    {
        DashboardGraph graph = CreateGraph(
            OrganizationRole.Member,
            "denied-membership-dashboard");
        if (deactivateMembership)
        {
            graph.Membership.Deactivate();
        }

        if (!includeMembership)
        {
            graph.Entities.RemoveAll(entity =>
                entity is OrganizationMembership or LegalTask or CalendarEvent);
        }

        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetDashboardPath(graph.Organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDashboard_InactiveUser_ReturnsNoStoreUnauthorized()
    {
        DashboardGraph graph = CreateGraph(
            OrganizationRole.Owner,
            "inactive-user-dashboard");
        graph.User.Deactivate();
        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetDashboardPath(graph.Organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDashboard_InactiveOrganization_ReturnsNoStoreForbidden()
    {
        DashboardGraph graph = CreateGraph(
            OrganizationRole.Owner,
            "inactive-organization-dashboard");
        graph.Organization.Deactivate();
        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetDashboardPath(graph.Organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDashboard_ZeroDataOrganization_ReturnsZerosAndEmptyGroups()
    {
        DashboardGraph graph = CreateGraph(
            OrganizationRole.Owner,
            "zero-data-dashboard");
        graph.Entities.RemoveAll(entity =>
            entity is ClientEntity or
                LegalProcess or
                LegalDeadline or
                LegalTask or
                CalendarEvent);
        string rawHandle = await SeedAuthenticatedAsync(graph);

        using HttpResponseMessage response = await SendGetAsync(
            GetDashboardPath(graph.Organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        GetDashboardResponse result = Assert.IsType<GetDashboardResponse>(
            await response.Content.ReadFromJsonAsync<GetDashboardResponse>());
        Assert.Equal(
            new DashboardSummaryResponse(0, 0, 0, 0),
            result.Summary);
        Assert.Equal(
            new DashboardAttentionResponse(
                new DashboardAttentionBucketResponse(0, 0, 0),
                new DashboardAttentionBucketResponse(0, 0, 0)),
            result.Attention);
        Assert.Empty(result.Upcoming.Deadlines);
        Assert.Empty(result.Upcoming.Tasks);
        Assert.Empty(result.Upcoming.CalendarEvents);
    }

    [Fact]
    public async Task GetDashboard_QueryIdentityOverridesAreIgnoredAndForeignDataNeverAppears()
    {
        DashboardGraph own = CreateGraph(
            OrganizationRole.Member,
            "identity-own-dashboard");
        DashboardGraph foreign = CreateGraph(
            OrganizationRole.Owner,
            "identity-foreign-dashboard");
        own.Entities.AddRange(foreign.Entities);
        string rawHandle = await SeedAuthenticatedAsync(own);
        string path = GetDashboardPath(own.Organization.Id) +
            $"?userId={foreign.User.Id:D}" +
            $"&organizationId={foreign.Organization.Id:D}" +
            $"&membershipId={foreign.Membership.Id:D}";

        using HttpResponseMessage response = await SendGetAsync(path, rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetDashboardResponse result = Assert.IsType<GetDashboardResponse>(
            await response.Content.ReadFromJsonAsync<GetDashboardResponse>());
        Assert.Equal(1, result.Summary.ActiveClients);
        Assert.Equal(own.Deadline.Id, Assert.Single(result.Upcoming.Deadlines).Id);
        Assert.Equal(own.Task.Id, Assert.Single(result.Upcoming.Tasks).Id);
        Assert.Equal(
            own.CalendarEvent.Id,
            Assert.Single(result.Upcoming.CalendarEvents).Id);
        Assert.DoesNotContain(
            result.Upcoming.Deadlines,
            item => item.Id == foreign.Deadline.Id);
        Assert.DoesNotContain(
            result.Upcoming.Tasks,
            item => item.Id == foreign.Task.Id);
        Assert.DoesNotContain(
            result.Upcoming.CalendarEvents,
            item => item.Id == foreign.CalendarEvent.Id);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties()
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToArray();
    }

    private static void AssertNoUnwantedFields(JsonElement element)
    {
        string[] forbiddenNames =
        [
            "organizationId",
            "userId",
            "membershipId",
            "clientId",
            "processId",
            "assigneeMembershipId",
            "description",
            "completedAt",
            "createdAt"
        ];

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbiddenNames);
                AssertNoUnwantedFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertNoUnwantedFields(item);
            }
        }
    }

    private static DashboardGraph CreateGraph(
        OrganizationRole role,
        string marker)
    {
        string unique = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            $"{marker} organization",
            $"{marker}-{unique}",
            Now.AddDays(-2));
        var user = new User(
            $"{marker} user",
            $"{marker}-{unique}@example.test",
            Now.AddDays(-2));
        user.VerifyEmail(Now.AddDays(-1));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddDays(-2));
        var legalClient = new ClientEntity(
            organization.Id,
            $"{marker} client",
            Now.AddDays(-2));
        var legalProcess = new LegalProcess(
            organization.Id,
            legalClient.Id,
            $"{marker} process",
            Now.AddDays(-2));
        var deadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            $"{marker} deadline",
            ReferenceDate,
            Now.AddDays(-2));
        var legalTask = new LegalTask(
            organization.Id,
            $"{marker} task",
            null,
            ReferenceDate.AddDays(1),
            legalProcess.Id,
            membership.Id,
            membership.Id,
            Now.AddDays(-2));
        var calendarEvent = new CalendarEvent(
            organization.Id,
            $"{marker} event",
            null,
            Now.AddHours(-1),
            Now.AddHours(1),
            null,
            null,
            legalProcess.Id,
            membership.Id,
            membership.Id,
            Now.AddDays(-2));

        return new DashboardGraph(
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

    private async Task<string> SeedAuthenticatedAsync(DashboardGraph graph)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            graph.User.Id,
            PasswordHash,
            Now.AddHours(-2));
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

    private static string GetDashboardPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/dashboard";
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

    private sealed record DashboardGraph(
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
