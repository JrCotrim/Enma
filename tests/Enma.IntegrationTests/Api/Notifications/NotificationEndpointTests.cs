using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Enma.Api.Contracts.Notifications;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Notifications;
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

namespace Enma.IntegrationTests.Api.Notifications;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-notification-http-password-hash";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T18:00:00Z");
    private static readonly DateTimeOffset GeneratedAt = Now.AddMinutes(-10);
    private static readonly DateOnly OccurrenceDate = new(2026, 8, 26);
    private static readonly DateTimeOffset OccurrenceAt = Now.AddHours(2);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public NotificationEndpointTests(PostgreSqlFixture fixture)
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
    public void Contracts_ExposeOnlyApprovedFieldsAndStableStrings()
    {
        Assert.Equal(
            [nameof(ListNotificationsResponse.Items),
             nameof(ListNotificationsResponse.UnreadCount)],
            GetPropertyNames<ListNotificationsResponse>());
        Assert.Equal(
            [
                nameof(NotificationResponse.Id),
                nameof(NotificationResponse.Kind),
                nameof(NotificationResponse.SourceType),
                nameof(NotificationResponse.SourceId),
                nameof(NotificationResponse.SourceTitle),
                nameof(NotificationResponse.OccurrenceDate),
                nameof(NotificationResponse.OccurrenceAt),
                nameof(NotificationResponse.GeneratedAt),
                nameof(NotificationResponse.ReadAt)
            ],
            GetPropertyNames<NotificationResponse>());
        Assert.Equal(
            [
                "legalDeadlineDueSoon",
                "legalTaskDueSoon",
                "calendarEventStartingSoon"
            ],
            Enum.GetValues<NotificationKindResponse>()
                .Select(value => JsonSerializer.Serialize(value).Trim('"')));
        Assert.Equal(
            ["legalDeadline", "legalTask", "calendarEvent"],
            Enum.GetValues<NotificationSourceTypeResponse>()
                .Select(value => JsonSerializer.Serialize(value).Trim('"')));
        Assert.DoesNotContain(
            typeof(NotificationResponse).GetProperties(),
            property => property.Name is "RecipientUserId" or
                "OrganizationId" or "MembershipId" or "Description");
    }

    [Fact]
    public async Task List_Anonymous_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetListPath(Guid.NewGuid()));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task List_ActiveRole_ReturnsOwnCurrentSourceDataOnly(
        OrganizationRole role)
    {
        ApiGraph graph = CreateGraph("list", role);
        Notification deadline = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph,
            graph.Actor.Id,
            GeneratedAt.AddMinutes(2));
        Notification task = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph,
            graph.Actor.Id,
            GeneratedAt.AddMinutes(1));
        task.MarkAsRead(Now.AddMinutes(-1));
        Notification calendarEvent = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            graph,
            graph.Actor.Id,
            GeneratedAt);
        Notification otherUser = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph,
            graph.OtherUser.Id,
            GeneratedAt.AddMinutes(3));
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([deadline, task, calendarEvent, otherUser]));

        using HttpResponseMessage response = await SendGetAsync(
            GetListPath(graph.Organization.Id),
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string json = await response.Content.ReadAsStringAsync();
        ListNotificationsResponse result = Assert.IsType<ListNotificationsResponse>(
            JsonSerializer.Deserialize<ListNotificationsResponse>(
                json,
                JsonSerializerOptions.Web));
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(2, result.UnreadCount);
        Assert.DoesNotContain(result.Items, item => item.Id == otherUser.Id);

        NotificationResponse deadlineResponse = Assert.Single(
            result.Items,
            item => item.Id == deadline.Id);
        Assert.Equal(NotificationKindResponse.LegalDeadlineDueSoon,
            deadlineResponse.Kind);
        Assert.Equal(NotificationSourceTypeResponse.LegalDeadline,
            deadlineResponse.SourceType);
        Assert.Equal(graph.Deadline.Id, deadlineResponse.SourceId);
        Assert.Equal(graph.Deadline.Title, deadlineResponse.SourceTitle);
        Assert.Equal(OccurrenceDate, deadlineResponse.OccurrenceDate);
        Assert.Null(deadlineResponse.OccurrenceAt);

        NotificationResponse taskResponse = Assert.Single(
            result.Items,
            item => item.Id == task.Id);
        Assert.Equal(NotificationSourceTypeResponse.LegalTask,
            taskResponse.SourceType);
        Assert.Equal(Now.AddMinutes(-1), taskResponse.ReadAt);

        NotificationResponse eventResponse = Assert.Single(
            result.Items,
            item => item.Id == calendarEvent.Id);
        Assert.Equal(NotificationSourceTypeResponse.CalendarEvent,
            eventResponse.SourceType);
        Assert.Null(eventResponse.OccurrenceDate);
        Assert.Equal(OccurrenceAt, eventResponse.OccurrenceAt);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement firstItem = document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .First();
        Assert.False(firstItem.TryGetProperty("organizationId", out _));
        Assert.False(firstItem.TryGetProperty("recipientUserId", out _));
        Assert.False(firstItem.TryGetProperty("membershipId", out _));
        Assert.False(firstItem.TryGetProperty("description", out _));
    }

    [Theory]
    [InlineData(AccessState.NoMembership, HttpStatusCode.Forbidden)]
    [InlineData(AccessState.InactiveMembership, HttpStatusCode.Forbidden)]
    [InlineData(AccessState.InactiveOrganization, HttpStatusCode.Forbidden)]
    [InlineData(AccessState.InactiveUser, HttpStatusCode.Unauthorized)]
    public async Task List_WithoutLiveAccess_IsDenied(
        AccessState state,
        HttpStatusCode expectedStatus)
    {
        Organization organization = CreateOrganization($"access-{state}");
        User actor = CreateUser($"access-{state}");
        var entities = new List<object> { organization, actor };

        if (state != AccessState.NoMembership)
        {
            var membership = new OrganizationMembership(
                organization.Id,
                actor.Id,
                OrganizationRole.Member,
                Now.AddDays(-1));
            if (state == AccessState.InactiveMembership)
            {
                membership.Deactivate();
            }

            entities.Add(membership);
        }

        if (state == AccessState.InactiveOrganization)
        {
            organization.Deactivate();
        }

        if (state == AccessState.InactiveUser)
        {
            actor.Deactivate();
        }

        string rawHandle = await SeedAuthenticatedAsync(actor, entities);

        using HttpResponseMessage response = await SendGetAsync(
            GetListPath(organization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, expectedStatus);
    }

    [Fact]
    public async Task List_ClientSuppliedRecipientQueryCannotSelectAnotherUser()
    {
        ApiGraph graph = CreateGraph("recipient-override", OrganizationRole.Owner);
        Notification own = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph,
            graph.Actor.Id);
        Notification other = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph,
            graph.OtherUser.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([own, other]));

        using HttpResponseMessage response = await SendGetAsync(
            GetListPath(graph.Organization.Id) +
                $"?recipientUserId={graph.OtherUser.Id:D}",
            rawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListNotificationsResponse result = Assert.IsType<ListNotificationsResponse>(
            await response.Content.ReadFromJsonAsync<ListNotificationsResponse>());
        Assert.Equal(own.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.UnreadCount);
    }

    [Fact]
    public async Task MarkOne_OwnNotification_IsBodylessIdempotentAndPreservesFirstRead()
    {
        ApiGraph graph = CreateGraph("mark-one", OrganizationRole.Member);
        Notification notification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph,
            graph.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([notification]));
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage firstResponse = await SendMutationAsync(
            GetMarkOnePath(graph.Organization.Id, notification.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(firstResponse, HttpStatusCode.NoContent);
        Assert.Equal(Now, await GetReadAtAsync(notification.Id));

        using HttpResponseMessage repeatedResponse = await SendMutationAsync(
            GetMarkOnePath(graph.Organization.Id, notification.Id),
            rawHandle,
            csrf);
        await AssertEmptyResponseAsync(repeatedResponse, HttpStatusCode.NoContent);
        Assert.Equal(Now, await GetReadAtAsync(notification.Id));
    }

    [Theory]
    [InlineData(HiddenNotificationKind.Nonexistent)]
    [InlineData(HiddenNotificationKind.SameTenantOtherUser)]
    [InlineData(HiddenNotificationKind.OtherTenant)]
    public async Task MarkOne_HiddenNotification_ReturnsSameNotFound(
        HiddenNotificationKind hiddenKind)
    {
        ApiGraph own = CreateGraph("hidden-own", OrganizationRole.Owner);
        ApiGraph foreign = CreateGraph("hidden-foreign", OrganizationRole.Owner);
        Notification otherUser = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own,
            own.OtherUser.Id);
        Notification otherTenant = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            foreign,
            foreign.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            own.Actor,
            own.Entities
                .Concat(foreign.Entities)
                .Concat([otherUser, otherTenant]));
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        Guid notificationId = hiddenKind switch
        {
            HiddenNotificationKind.Nonexistent => Guid.NewGuid(),
            HiddenNotificationKind.SameTenantOtherUser => otherUser.Id,
            HiddenNotificationKind.OtherTenant => otherTenant.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(hiddenKind))
        };

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkOnePath(own.Organization.Id, notificationId),
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NotFound);
        Assert.Null(await GetReadAtAsync(otherUser.Id));
        Assert.Null(await GetReadAtAsync(otherTenant.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkOne_MissingOrInvalidAntiforgery_IsRejected(bool invalid)
    {
        ApiGraph graph = CreateGraph("mark-one-csrf", OrganizationRole.Member);
        Notification notification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph,
            graph.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([notification]));
        CsrfPair? csrf = invalid ? await GetCsrfPairAsync(rawHandle) : null;

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkOnePath(graph.Organization.Id, notification.Id),
            rawHandle,
            csrf,
            invalid ? "invalid-token" : null);

        await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
        Assert.Null(await GetReadAtAsync(notification.Id));
    }

    [Fact]
    public async Task MarkOne_Anonymous_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Put,
                GetMarkOnePath(Guid.NewGuid(), Guid.NewGuid())));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MarkOne_InactiveMembership_ReturnsNoStoreForbidden()
    {
        ApiGraph graph = CreateGraph("mark-one-inactive", OrganizationRole.Member);
        graph.Membership.Deactivate();
        Notification notification = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            graph,
            graph.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([notification]));
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkOnePath(graph.Organization.Id, notification.Id),
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Null(await GetReadAtAsync(notification.Id));
    }

    [Fact]
    public async Task MarkAll_UpdatesOnlyOwnUnreadAndPreservesExistingReadAt()
    {
        ApiGraph own = CreateGraph("mark-all-own", OrganizationRole.Administrator);
        ApiGraph foreign = CreateGraph("mark-all-foreign", OrganizationRole.Owner);
        Notification ownDeadline = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            own,
            own.Actor.Id);
        Notification ownTask = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own,
            own.Actor.Id);
        DateTimeOffset firstReadAt = Now.AddMinutes(-2);
        ownTask.MarkAsRead(firstReadAt);
        Notification otherUser = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            own,
            own.OtherUser.Id);
        Notification otherTenant = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            foreign,
            foreign.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            own.Actor,
            own.Entities
                .Concat(foreign.Entities)
                .Concat([ownDeadline, ownTask, otherUser, otherTenant]));
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkAllPath(own.Organization.Id),
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(Now, await GetReadAtAsync(ownDeadline.Id));
        Assert.Equal(firstReadAt, await GetReadAtAsync(ownTask.Id));
        Assert.Null(await GetReadAtAsync(otherUser.Id));
        Assert.Null(await GetReadAtAsync(otherTenant.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkAll_MissingOrInvalidAntiforgery_IsRejected(bool invalid)
    {
        ApiGraph graph = CreateGraph("mark-all-csrf", OrganizationRole.Owner);
        Notification notification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph,
            graph.Actor.Id);
        string rawHandle = await SeedAuthenticatedAsync(
            graph.Actor,
            graph.Entities.Concat([notification]));
        CsrfPair? csrf = invalid ? await GetCsrfPairAsync(rawHandle) : null;

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkAllPath(graph.Organization.Id),
            rawHandle,
            csrf,
            invalid ? "invalid-token" : null);

        await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
        Assert.Null(await GetReadAtAsync(notification.Id));
    }

    [Fact]
    public async Task MarkAll_Anonymous_ReturnsNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Put,
                GetMarkAllPath(Guid.NewGuid())));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MarkAll_NoMembership_ReturnsNoStoreForbidden()
    {
        Organization organization = CreateOrganization("mark-all-no-membership");
        User actor = CreateUser("mark-all-no-membership");
        string rawHandle = await SeedAuthenticatedAsync(
            actor,
            [organization, actor]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            GetMarkAllPath(organization.Id),
            rawHandle,
            csrf);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties()
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToArray();
    }

    private async Task<string> SeedAuthenticatedAsync(
        User actor,
        IEnumerable<object> entities)
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
        dbContext.AddRange(entities);
        dbContext.UserCredentials.Add(credential);
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

    private async Task<HttpResponseMessage> SendGetAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(
        string path,
        string rawHandle,
        CsrfPair? csrf,
        string? requestTokenOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        var cookies = new List<string> { $"{SessionCookieName}={rawHandle}" };
        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        string? requestToken = requestTokenOverride ?? csrf?.RequestToken;
        if (requestToken is not null)
        {
            request.Headers.Add(CsrfHeaderName, requestToken);
        }

        return await client.SendAsync(request);
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

    private async Task<DateTimeOffset?> GetReadAtAsync(Guid notificationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Notifications
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.ReadAt)
            .SingleAsync();
    }

    private static ApiGraph CreateGraph(
        string marker,
        OrganizationRole actorRole)
    {
        Organization organization = CreateOrganization(marker);
        User actor = CreateUser($"{marker}-actor");
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            actorRole,
            Now.AddDays(-1));
        User otherUser = CreateUser($"{marker}-other");
        var otherMembership = new OrganizationMembership(
            organization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            Now.AddDays(-1));
        var clientEntity = new Client(
            organization.Id,
            $"Client {marker}",
            Now.AddDays(-1));
        var process = new LegalProcess(
            organization.Id,
            clientEntity.Id,
            $"Process {marker}",
            Now.AddDays(-1));
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            $"Deadline {marker}",
            OccurrenceDate,
            Now.AddDays(-1));
        var task = new LegalTask(
            organization.Id,
            $"Task {marker}",
            null,
            OccurrenceDate,
            process.Id,
            membership.Id,
            membership.Id,
            Now.AddDays(-1));
        var calendarEvent = new CalendarEvent(
            organization.Id,
            $"Event {marker}",
            null,
            OccurrenceAt,
            OccurrenceAt.AddHours(1),
            null,
            null,
            process.Id,
            membership.Id,
            membership.Id,
            Now.AddDays(-1));

        return new ApiGraph(
            organization,
            actor,
            membership,
            otherUser,
            otherMembership,
            clientEntity,
            process,
            deadline,
            task,
            calendarEvent,
            [
                organization,
                actor,
                membership,
                otherUser,
                otherMembership,
                clientEntity,
                process,
                deadline,
                task,
                calendarEvent
            ]);
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"Notification {marker}",
            $"notification-{marker}-{Guid.NewGuid():N}".ToLowerInvariant(),
            Now.AddDays(-1));
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Notification {marker}",
            $"notification-{marker}-{Guid.NewGuid():N}@example.test"
                .ToLowerInvariant(),
            Now.AddDays(-1));
        user.VerifyEmail(Now.AddHours(-12));
        return user;
    }

    private static Notification CreateNotification(
        NotificationKind kind,
        ApiGraph graph,
        Guid recipientUserId,
        DateTimeOffset? generatedAt = null)
    {
        return kind switch
        {
            NotificationKind.LegalDeadlineDueSoon => new Notification(
                graph.Organization.Id,
                recipientUserId,
                kind,
                graph.Deadline.Id,
                null,
                null,
                OccurrenceDate,
                null,
                generatedAt ?? GeneratedAt),
            NotificationKind.LegalTaskDueSoon => new Notification(
                graph.Organization.Id,
                recipientUserId,
                kind,
                null,
                graph.Task.Id,
                null,
                OccurrenceDate,
                null,
                generatedAt ?? GeneratedAt),
            NotificationKind.CalendarEventStartingSoon => new Notification(
                graph.Organization.Id,
                recipientUserId,
                kind,
                null,
                null,
                graph.CalendarEvent.Id,
                null,
                OccurrenceAt,
                generatedAt ?? GeneratedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string GetListPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/notifications";
    }

    private static string GetMarkOnePath(
        Guid organizationId,
        Guid notificationId)
    {
        return $"{GetListPath(organizationId)}/{notificationId:D}/read";
    }

    private static string GetMarkAllPath(Guid organizationId)
    {
        return $"{GetListPath(organizationId)}/read-all";
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

    public enum AccessState
    {
        NoMembership = 0,
        InactiveMembership = 1,
        InactiveOrganization = 2,
        InactiveUser = 3
    }

    public enum HiddenNotificationKind
    {
        Nonexistent = 0,
        SameTenantOtherUser = 1,
        OtherTenant = 2
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);

    private sealed record ApiGraph(
        Organization Organization,
        User Actor,
        OrganizationMembership Membership,
        User OtherUser,
        OrganizationMembership OtherMembership,
        Client Client,
        LegalProcess Process,
        LegalDeadline Deadline,
        LegalTask Task,
        CalendarEvent CalendarEvent,
        IReadOnlyList<object> Entities);
}
