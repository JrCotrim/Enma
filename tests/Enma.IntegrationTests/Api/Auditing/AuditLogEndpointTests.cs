using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Enma.Api.Contracts.Auditing;
using Enma.Application.Auditing.List;
using Enma.Application.Authentication;
using Enma.Domain.Auditing;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Auditing;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuditLogEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-audit-read-password-hash";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-29T15:00:00Z");

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public AuditLogEndpointTests(PostgreSqlFixture fixture)
    {
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
    public async Task List_Anonymous_ReturnsEmptyNoStoreUnauthorized()
    {
        using HttpResponseMessage response = await client.GetAsync(
            GetPath(Guid.NewGuid()));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task List_PrivilegedRole_ReturnsTenantAuditLog(
        OrganizationRole role)
    {
        TestActor actor = CreateActor($"Allowed {role}", role);
        AuditLog auditLog = CreateAuditLog(
            actor,
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            AuditEventType.ClientCreated,
            Guid.NewGuid());
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization],
            [actor.User],
            [actor.Membership],
            [auditLog]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(actor.Organization.Id),
            rawHandle);
        ListAuditLogsResponse page = await ReadPageAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(auditLog.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task List_ExposesOnlyClosedPublicDetailsWithoutInternalIdentifiers()
    {
        TestActor actor = CreateActor("Details", OrganizationRole.Owner);
        Guid oldAssignee = Guid.Parse(
            "1a19a8fe-37cb-4445-8d1e-b66fb5a1ce5f");
        Guid newAssignee = Guid.Parse(
            "10416bb3-2ea9-4c4a-a9c8-dc2094eb92f4");
        AuditLog[] logs =
        [
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000001"),
                AuditEventType.ClientCreated,
                Guid.NewGuid()),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000002"),
                AuditEventType.OrganizationRenamed,
                actor.Organization.Id,
                new OrganizationRenamedAuditDetails("Old Legal", "New Legal")),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000003"),
                AuditEventType.OrganizationMembershipRoleChanged,
                Guid.NewGuid(),
                new OrganizationMembershipRoleChangedAuditDetails(
                    OrganizationRole.Member,
                    OrganizationRole.Administrator)),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000004"),
                AuditEventType.LegalDeadlineDetailsChanged,
                Guid.NewGuid(),
                new LegalDeadlineDetailsChangedAuditDetails(
                    [LegalDeadlineChangedField.Title])),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000005"),
                AuditEventType.LegalTaskDetailsChanged,
                Guid.NewGuid(),
                new LegalTaskDetailsChangedAuditDetails(
                    [LegalTaskChangedField.Description])),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000006"),
                AuditEventType.LegalTaskAssigneeChanged,
                Guid.NewGuid(),
                new LegalTaskAssigneeChangedAuditDetails(
                    oldAssignee,
                    newAssignee)),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000007"),
                AuditEventType.CalendarEventUpdated,
                Guid.NewGuid(),
                new CalendarEventUpdatedAuditDetails(
                    [CalendarEventChangedField.Location])),
            CreateAuditLog(
                actor,
                Guid.Parse("41000000-0000-0000-0000-000000000008"),
                AuditEventType.CalendarEventAssigneeChanged,
                Guid.NewGuid(),
                new CalendarEventAssigneeChangedAuditDetails(
                    null,
                    newAssignee))
        ];
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization],
            [actor.User],
            [actor.Membership],
            logs);

        using HttpResponseMessage response = await SendAsync(
            GetPath(actor.Organization.Id),
            rawHandle);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] items = document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8, items.Length);
        Assert.All(
            items,
            item => Assert.Equal(
                [
                    "id",
                    "actorMembershipId",
                    "actorRoleAtOccurrence",
                    "eventType",
                    "entityType",
                    "entityId",
                    "occurredAt",
                    "details"
                ],
                item.EnumerateObject().Select(property => property.Name)));
        Assert.Equal(
            JsonValueKind.Null,
            FindByEventType(items, "client.created")
                .GetProperty("details")
                .ValueKind);
        AssertDetailProperties(
            items,
            "organization.renamed",
            "type",
            "oldName",
            "newName");
        AssertDetailProperties(
            items,
            "organization_membership.role_changed",
            "type",
            "oldRole",
            "newRole");
        AssertDetailProperties(
            items,
            "legal_deadline.details_changed",
            "type",
            "changedFields");
        AssertDetailProperties(
            items,
            "legal_task.details_changed",
            "type",
            "changedFields");
        AssertDetailProperties(
            items,
            "legal_task.assignee_changed",
            "type",
            "oldAssigneeMembershipId",
            "newAssigneeMembershipId");
        AssertDetailProperties(
            items,
            "calendar_event.updated",
            "type",
            "changedFields");
        AssertDetailProperties(
            items,
            "calendar_event.assignee_changed",
            "type",
            "oldAssigneeMembershipId",
            "newAssigneeMembershipId");
        Assert.DoesNotContain("organizationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("actorUserId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("traceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditLogDetailsResponse", json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", json, StringComparison.Ordinal);
        Assert.DoesNotContain("filename", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator, "Administrator")]
    [InlineData(OrganizationRole.Member, "Member")]
    public async Task List_OrganizationInvitationCreated_ReturnsClosedPublicDetails(
        OrganizationRole invitedRole,
        string expectedRole)
    {
        TestActor actor = CreateActor("Invitation Created", OrganizationRole.Owner);
        AuditLog auditLog = CreateAuditLog(
            actor,
            Guid.Parse("41100000-0000-0000-0000-000000000001"),
            AuditEventType.OrganizationInvitationCreated,
            Guid.NewGuid(),
            new OrganizationInvitationCreatedAuditDetails(invitedRole));
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization],
            [actor.User],
            [actor.Membership],
            [auditLog]);

        using HttpResponseMessage response = await SendAsync(
            GetPath(actor.Organization.Id),
            rawHandle);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement item = Assert.Single(document.RootElement
            .GetProperty("items")
            .EnumerateArray());
        JsonElement details = item.GetProperty("details");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "organization_invitation.created",
            item.GetProperty("eventType").GetString());
        Assert.Equal(
            "organization_invitation",
            item.GetProperty("entityType").GetString());
        Assert.Equal(
            ["type", "role"],
            details.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "organization_invitation.created",
            details.GetProperty("type").GetString());
        Assert.Equal(expectedRole, details.GetProperty("role").GetString());
        Assert.DoesNotContain("organizationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("actorUserId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("traceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_InvitationEventsWithoutDetails_ReturnNullDetails()
    {
        TestActor actor = CreateActor("Invitation Null", OrganizationRole.Owner);
        AuditEventType[] eventTypes =
        [
            AuditEventType.OrganizationInvitationRevoked,
            AuditEventType.OrganizationInvitationAccepted,
            AuditEventType.OrganizationInvitationResent
        ];
        AuditLog[] auditLogs = eventTypes
            .Select((eventType, index) => CreateAuditLog(
                actor,
                Guid.Parse($"41200000-0000-0000-0000-{index + 1:D12}"),
                eventType,
                Guid.NewGuid()))
            .ToArray();
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization],
            [actor.User],
            [actor.Membership],
            auditLogs);

        using HttpResponseMessage response = await SendAsync(
            GetPath(actor.Organization.Id),
            rawHandle);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] items = document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, items.Length);

        foreach (AuditEventType eventType in eventTypes)
        {
            JsonElement item = FindByEventType(items, eventType.ToCode());
            Assert.Equal(
                "organization_invitation",
                item.GetProperty("entityType").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("details").ValueKind);
        }
    }

    [Fact]
    public async Task List_PaginatesAndFiltersTenantDataThroughHttpContract()
    {
        TestActor actor = CreateActor("Paging", OrganizationRole.Administrator);
        TestActor foreign = CreateActor("Paging Foreign", OrganizationRole.Owner);
        Guid sharedEntityId = Guid.Parse(
            "02772a37-9d86-4260-8026-8d2162033100");
        AuditLog[] currentLogs =
        [
            CreateAuditLog(
                actor,
                Guid.Parse("42000000-0000-0000-0000-000000000001"),
                AuditEventType.ClientCreated,
                sharedEntityId),
            CreateAuditLog(
                actor,
                Guid.Parse("42000000-0000-0000-0000-000000000002"),
                AuditEventType.ClientCreated,
                Guid.NewGuid()),
            CreateAuditLog(
                actor,
                Guid.Parse("42000000-0000-0000-0000-000000000003"),
                AuditEventType.LegalProcessCreated,
                Guid.NewGuid())
        ];
        AuditLog foreignLog = CreateAuditLog(
            foreign,
            Guid.Parse("42000000-0000-0000-0000-000000000004"),
            AuditEventType.ClientCreated,
            sharedEntityId);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization, foreign.Organization],
            [actor.User, foreign.User],
            [actor.Membership, foreign.Membership],
            [.. currentLogs, foreignLog]);

        using HttpResponseMessage defaultResponse = await SendAsync(
            GetPath(actor.Organization.Id),
            rawHandle);
        using HttpResponseMessage maximumResponse = await SendAsync(
            $"{GetPath(actor.Organization.Id)}?pageSize=100",
            rawHandle);
        using HttpResponseMessage emptyResponse = await SendAsync(
            $"{GetPath(actor.Organization.Id)}?pageNumber=2&pageSize=100",
            rawHandle);
        using HttpResponseMessage eventResponse = await SendAsync(
            $"{GetPath(actor.Organization.Id)}?eventType=client.created",
            rawHandle);
        using HttpResponseMessage entityResponse = await SendAsync(
            $"{GetPath(actor.Organization.Id)}?entityType=client&entityId={sharedEntityId:D}",
            rawHandle);
        ListAuditLogsResponse defaultPage = await ReadPageAsync(defaultResponse);
        ListAuditLogsResponse maximumPage = await ReadPageAsync(maximumResponse);
        ListAuditLogsResponse emptyPage = await ReadPageAsync(emptyResponse);
        ListAuditLogsResponse eventPage = await ReadPageAsync(eventResponse);
        ListAuditLogsResponse entityPage = await ReadPageAsync(entityResponse);

        Guid[] expected = currentLogs
            .OrderByDescending(auditLog => auditLog.OccurredAt)
            .ThenByDescending(auditLog => auditLog.Id)
            .Select(auditLog => auditLog.Id)
            .ToArray();
        Assert.Equal(expected, defaultPage.Items.Select(item => item.Id));
        Assert.Equal(20, defaultPage.PageSize);
        Assert.Equal(3, defaultPage.TotalCount);
        Assert.Equal(100, maximumPage.PageSize);
        Assert.Equal(3, maximumPage.TotalCount);
        Assert.Empty(emptyPage.Items);
        Assert.Equal(3, emptyPage.TotalCount);
        Assert.Equal(2, eventPage.TotalCount);
        Assert.Equal(currentLogs[0].Id, Assert.Single(entityPage.Items).Id);
        Assert.Equal(1, entityPage.TotalCount);
    }

    [Theory]
    [InlineData(DeniedVariant.Member, HttpStatusCode.Forbidden)]
    [InlineData(DeniedVariant.InactiveMembership, HttpStatusCode.Forbidden)]
    [InlineData(DeniedVariant.InactiveUser, HttpStatusCode.Unauthorized)]
    [InlineData(DeniedVariant.InactiveOrganization, HttpStatusCode.Forbidden)]
    [InlineData(DeniedVariant.ForeignOrganization, HttpStatusCode.Forbidden)]
    [InlineData(DeniedVariant.NoMembership, HttpStatusCode.Forbidden)]
    public async Task List_DeniedActorState_ReturnsNoData(
        DeniedVariant variant,
        HttpStatusCode expectedStatusCode)
    {
        TestActor actor = CreateActor(
            $"Denied {variant}",
            variant == DeniedVariant.Member
                ? OrganizationRole.Member
                : OrganizationRole.Owner);
        Organization requestedOrganization = actor.Organization;
        OrganizationMembership[] memberships = [actor.Membership];
        Organization[] organizations = [actor.Organization];

        switch (variant)
        {
            case DeniedVariant.InactiveMembership:
                actor.Membership.Deactivate();
                break;
            case DeniedVariant.InactiveUser:
                actor.User.Deactivate();
                break;
            case DeniedVariant.InactiveOrganization:
                actor.Organization.Deactivate();
                break;
            case DeniedVariant.ForeignOrganization:
                requestedOrganization = CreateOrganization("Foreign Requested");
                organizations = [actor.Organization, requestedOrganization];
                break;
            case DeniedVariant.NoMembership:
                memberships = [];
                break;
        }

        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            organizations,
            [actor.User],
            memberships,
            []);

        using HttpResponseMessage response = await SendAsync(
            GetPath(requestedOrganization.Id),
            rawHandle);

        await AssertEmptyResponseAsync(response, expectedStatusCode);
    }

    [Theory]
    [InlineData("eventType=unknown")]
    [InlineData("eventType=Client.created")]
    [InlineData("entityType=client")]
    [InlineData("entityId=02772a37-9d86-4260-8026-8d2162033100")]
    [InlineData("entityType=unknown&entityId=02772a37-9d86-4260-8026-8d2162033100")]
    [InlineData("entityType=client&entityId=not-a-guid")]
    [InlineData("pageNumber=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageNumber=not-a-number")]
    public async Task List_InvalidQuery_ReturnsNoStoreBadRequest(string query)
    {
        TestActor actor = CreateActor("Invalid", OrganizationRole.Owner);
        string rawHandle = await SeedAuthenticatedCallerAsync(
            actor,
            [actor.Organization],
            [actor.User],
            [actor.Membership],
            []);

        using HttpResponseMessage response = await SendAsync(
            $"{GetPath(actor.Organization.Id)}?{query}",
            rawHandle);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditReadServices_AreRegisteredAsScoped()
    {
        await using AsyncServiceScope firstScope = factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = factory.Services.CreateAsyncScope();

        AssertScoped<ListAuditLogsUseCase>(firstScope, secondScope);
        AssertScoped<IAuditLogReadQueries>(firstScope, secondScope);
    }

    private static void AssertDetailProperties(
        IReadOnlyCollection<JsonElement> items,
        string eventType,
        params string[] expectedProperties)
    {
        JsonElement details = FindByEventType(items, eventType)
            .GetProperty("details");
        Assert.Equal(
            expectedProperties,
            details.EnumerateObject().Select(property => property.Name));
        Assert.Equal(eventType, details.GetProperty("type").GetString());
    }

    private static JsonElement FindByEventType(
        IEnumerable<JsonElement> items,
        string eventType)
    {
        return Assert.Single(
            items,
            item => item.GetProperty("eventType").GetString() == eventType);
    }

    private static void AssertScoped<TService>(
        AsyncServiceScope firstScope,
        AsyncServiceScope secondScope)
        where TService : class
    {
        TService first = firstScope.ServiceProvider.GetRequiredService<TService>();
        Assert.Same(
            first,
            firstScope.ServiceProvider.GetRequiredService<TService>());
        Assert.NotSame(
            first,
            secondScope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<string> SeedAuthenticatedCallerAsync(
        TestActor caller,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<AuditLog> auditLogs)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            caller.User.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            caller.User.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.AddRange(users);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.AuthenticationSessions.Add(session);
        dbContext.AuditLogs.AddRange(auditLogs);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static async Task<ListAuditLogsResponse> ReadPageAsync(
        HttpResponseMessage response)
    {
        ListAuditLogsResponse? page = await response.Content
            .ReadFromJsonAsync<ListAuditLogsResponse>();
        return Assert.IsType<ListAuditLogsResponse>(page);
    }

    private static TestActor CreateActor(string marker, OrganizationRole role)
    {
        Organization organization = CreateOrganization(marker);
        var user = new User(
            $"{marker} Actor",
            $"{marker.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1));
        return new TestActor(organization, user, membership);
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
    }

    private static AuditLog CreateAuditLog(
        TestActor actor,
        Guid id,
        AuditEventType eventType,
        Guid entityId,
        AuditEventDetails? details = null)
    {
        MethodInfo factory = typeof(AuditLog).GetMethod(
            "CreateAuthoritative",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "The authoritative audit factory was not found.");

        return (AuditLog)(factory.Invoke(
            null,
            [
                id,
                actor.Organization.Id,
                actor.User.Id,
                actor.Membership.Id,
                actor.Membership.Role,
                eventType,
                eventType.GetEntityType(),
                entityId,
                Now,
                details,
                "0123456789abcdef0123456789abcdef"
            ]) ?? throw new InvalidOperationException(
                "The authoritative audit factory returned no audit log."));
    }

    private static string GetPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/audit-logs";
    }

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    public enum DeniedVariant
    {
        Member,
        InactiveMembership,
        InactiveUser,
        InactiveOrganization,
        ForeignOrganization,
        NoMembership
    }

    private sealed record TestActor(
        Organization Organization,
        User User,
        OrganizationMembership Membership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
