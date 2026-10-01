using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Deadlines;
using Enma.Application.Authentication;
using Enma.Domain.Auditing;
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
public sealed class LegalDeadlineResponsibleEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-legal-deadline-responsible-endpoint-password-hash";
    private const string UnavailableTitle = "Related responsible member unavailable";
    private const string UnavailableDetail =
        "The requested responsible member is unavailable.";

    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        30,
        15,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateOnly DueDate = new(2026, 10, 20);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LegalDeadlineResponsibleEndpointTests(PostgreSqlFixture fixture)
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
    public async Task CreateLegalDeadline_LegacyBodyWithoutResponsible_CreatesWithOnlyCreationAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(graph.Organization.Id),
            graph.RawHandle,
            csrf,
            new
            {
                processId = graph.Process.Id,
                title = "Legacy Body Deadline",
                dueDate = "2026-10-20"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateLegalDeadlineResponse? created = await response.Content
            .ReadFromJsonAsync<CreateLegalDeadlineResponse>();
        Guid deadlineId = Assert.IsType<CreateLegalDeadlineResponse>(created).Id;
        Assert.Null((await GetPersistedDeadlineAsync(deadlineId)).ResponsibleMembershipId);
        Assert.Equal(
            [AuditEventType.LegalDeadlineCreated],
            await GetAuditEventTypesAsync(deadlineId));
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task CreateLegalDeadline_WithAvailableResponsible_PersistsWithOnlyCreationAudit(
        OrganizationRole actorRole)
    {
        TestGraph graph = await SeedGraphAsync(actorRole);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(graph.Organization.Id),
            graph.RawHandle,
            csrf,
            new
            {
                processId = graph.Process.Id,
                title = "Responsible Deadline",
                dueDate = "2026-10-20",
                responsibleMembershipId = graph.ResponsibleMembership.Id
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateLegalDeadlineResponse? created = await response.Content
            .ReadFromJsonAsync<CreateLegalDeadlineResponse>();
        Guid deadlineId = Assert.IsType<CreateLegalDeadlineResponse>(created).Id;
        Assert.Equal(
            graph.ResponsibleMembership.Id,
            (await GetPersistedDeadlineAsync(deadlineId)).ResponsibleMembershipId);
        Assert.Equal(
            [AuditEventType.LegalDeadlineCreated],
            await GetAuditEventTypesAsync(deadlineId));
    }

    [Fact]
    public async Task CreateLegalDeadline_UnavailableResponsible_ReturnsSameNeutralBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        Guid[] unavailableMembershipIds = await PrepareUnavailableMembershipsAsync(graph);

        foreach (Guid membershipId in unavailableMembershipIds)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Post,
                GetDeadlinesPath(graph.Organization.Id),
                graph.RawHandle,
                csrf,
                new
                {
                    processId = graph.Process.Id,
                    title = "Unavailable Responsible",
                    dueDate = "2026-10-20",
                    responsibleMembershipId = membershipId
                });

            ProblemDetails problem = await AssertSafeBadRequestAsync(response);
            Assert.Equal(UnavailableTitle, problem.Title);
            Assert.Equal(UnavailableDetail, problem.Detail);
            Assert.DoesNotContain(
                membershipId.ToString("D"),
                await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        await AssertNoDeadlineWritesAsync();
    }

    [Fact]
    public async Task CreateLegalDeadline_EmptyResponsible_ReturnsValidationBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetDeadlinesPath(graph.Organization.Id),
            graph.RawHandle,
            csrf,
            new
            {
                processId = graph.Process.Id,
                title = "Empty Responsible",
                dueDate = "2026-10-20",
                responsibleMembershipId = Guid.Empty
            });

        ProblemDetails problem = await AssertSafeBadRequestAsync(response);
        Assert.Equal("Invalid request data", problem.Title);
        await AssertNoDeadlineWritesAsync();
    }

    [Fact]
    public async Task ChangeResponsible_SetNoOpAndClear_ReturnNoContentAndAuditEffectiveChanges()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Administrator,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        string path = GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id);

        foreach (Guid? responsibleMembershipId in new Guid?[]
        {
            graph.ResponsibleMembership.Id,
            graph.ResponsibleMembership.Id,
            null,
            null
        })
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Put,
                path,
                graph.RawHandle,
                csrf,
                new { responsibleMembershipId });
            await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        }

        LegalDeadline persisted = await GetPersistedDeadlineAsync(graph.Deadline.Id);
        Assert.Null(persisted.ResponsibleMembershipId);
        Assert.Equal(graph.Deadline.Title, persisted.Title);
        Assert.Equal(graph.Deadline.DueDate, persisted.DueDate);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(
            [
                AuditEventType.LegalDeadlineResponsibleChanged,
                AuditEventType.LegalDeadlineResponsibleChanged
            ],
            await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_CompletedDeadline_IsAllowedAndKeepsCompletion()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            completedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf,
            new { responsibleMembershipId = graph.ResponsibleMembership.Id });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        LegalDeadline persisted = await GetPersistedDeadlineAsync(graph.Deadline.Id);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
        Assert.Equal(graph.Deadline.CompletedAt, persisted.CompletedAt);
        Assert.Equal(
            [AuditEventType.LegalDeadlineResponsibleChanged],
            await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_UnavailableResponsible_ReturnsSameNeutralBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        Guid[] unavailableMembershipIds = await PrepareUnavailableMembershipsAsync(graph);

        foreach (Guid membershipId in unavailableMembershipIds)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                HttpMethod.Put,
                GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id),
                graph.RawHandle,
                csrf,
                new { responsibleMembershipId = membershipId });

            ProblemDetails problem = await AssertSafeBadRequestAsync(response);
            Assert.Equal(UnavailableTitle, problem.Title);
            Assert.Equal(UnavailableDetail, problem.Detail);
        }

        Assert.Null(
            (await GetPersistedDeadlineAsync(graph.Deadline!.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_MissingOrCrossTenantDeadline_ReturnSameNotFound()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        TestGraph foreignGraph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            marker: "foreign");
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage crossTenant = await SendMutationAsync(
            HttpMethod.Put,
            GetResponsiblePath(graph.Organization.Id, foreignGraph.Deadline!.Id),
            graph.RawHandle,
            csrf,
            new { responsibleMembershipId = graph.ResponsibleMembership.Id });
        using HttpResponseMessage missing = await SendMutationAsync(
            HttpMethod.Put,
            GetResponsiblePath(graph.Organization.Id, Guid.NewGuid()),
            graph.RawHandle,
            csrf,
            new { responsibleMembershipId = graph.ResponsibleMembership.Id });

        await AssertEmptyResponseAsync(crossTenant, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missing, HttpStatusCode.NotFound);
        Assert.Null(
            (await GetPersistedDeadlineAsync(foreignGraph.Deadline.Id))
                .ResponsibleMembershipId);
        Assert.Empty(await GetAuditEventTypesAsync(foreignGraph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_MemberRole_ReturnsForbiddenWithoutMutation()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Member,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf,
            new { responsibleMembershipId = graph.ResponsibleMembership.Id });

        await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
        Assert.Null(
            (await GetPersistedDeadlineAsync(graph.Deadline.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_AnonymousOrWithoutValidCsrf_ReturnEmptyResponsesWithoutMutation()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        string path = GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id);
        object body = new { responsibleMembershipId = graph.ResponsibleMembership.Id };

        using HttpResponseMessage anonymous = await client.PutAsJsonAsync(path, body);
        using HttpResponseMessage missingCsrf = await SendMutationAsync(
            HttpMethod.Put,
            path,
            graph.RawHandle,
            csrf: null,
            body);
        using HttpResponseMessage invalidCsrf = await SendMutationAsync(
            HttpMethod.Put,
            path,
            graph.RawHandle,
            csrf,
            body,
            requestTokenOverride: "malformed");

        await AssertEmptyResponseAsync(anonymous, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(missingCsrf, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(invalidCsrf, HttpStatusCode.BadRequest);
        Assert.Null(
            (await GetPersistedDeadlineAsync(graph.Deadline.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_InvalidBodies_ReturnSafeBadRequestWithoutMutation()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        string path = GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id);

        foreach (string json in new[]
        {
            "{",
            "{}",
            """{"responsible":null}""",
            $$"""{"responsibleMembershipId":"{{Guid.Empty:D}}"}""",
            """{"responsibleMembershipId":"not-a-guid"}""",
            """{"responsibleMembershipId":42}"""
        })
        {
            using HttpResponseMessage response = await SendRawJsonAsync(
                HttpMethod.Put,
                path,
                graph.RawHandle,
                csrf,
                json);

            await AssertSafeBadRequestAsync(response);
        }

        Assert.Null(
            (await GetPersistedDeadlineAsync(graph.Deadline.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Theory]
    [InlineData(ResponsibleState.None, HttpStatusCode.NoContent)]
    [InlineData(ResponsibleState.Active, HttpStatusCode.NoContent)]
    [InlineData(ResponsibleState.InactiveMembership, HttpStatusCode.Conflict)]
    [InlineData(ResponsibleState.InactiveUser, HttpStatusCode.Conflict)]
    public async Task ReopenLegalDeadline_WithResponsible_RequiresAvailableCurrentResponsible(
        ResponsibleState state,
        HttpStatusCode expectedStatusCode)
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            completedDeadline: true,
            deadlineResponsible: state != ResponsibleState.None);
        await ApplyResponsibleStateAsync(graph, state);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetReopenPath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf);

        LegalDeadline persisted = await GetPersistedDeadlineAsync(graph.Deadline.Id);
        Assert.Equal(graph.Deadline.ResponsibleMembershipId, persisted.ResponsibleMembershipId);

        if (expectedStatusCode == HttpStatusCode.NoContent)
        {
            await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
            Assert.Null(persisted.CompletedAt);
            Assert.Equal(
                [AuditEventType.LegalDeadlineReopened],
                await GetAuditEventTypesAsync(graph.Deadline.Id));
            return;
        }

        ProblemDetails problem = await AssertConflictProblemAsync(response);
        Assert.Equal("Resource conflict", problem.Title);
        Assert.Contains("responsible", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(graph.Deadline.CompletedAt, persisted.CompletedAt);
        Assert.Empty(await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task CompleteLegalDeadline_WithUnavailableResponsible_StillCompletes()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            deadlineResponsible: true);
        await ApplyResponsibleStateAsync(graph, ResponsibleState.InactiveUser);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Post,
            GetCompletePath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf);

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        LegalDeadline persisted = await GetPersistedDeadlineAsync(graph.Deadline.Id);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task UpdateLegalDeadline_TitleAndDueDate_PreservesResponsible()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            deadlineResponsible: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            HttpMethod.Put,
            GetDeadlinePath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf,
            new
            {
                title = "Updated With Responsible",
                dueDate = "2026-10-25",
                responsibleMembershipId = (Guid?)null
            });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        LegalDeadline persisted = await GetPersistedDeadlineAsync(graph.Deadline.Id);
        Assert.Equal("Updated With Responsible", persisted.Title);
        Assert.Equal(new DateOnly(2026, 10, 25), persisted.DueDate);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
        Assert.Equal(
            [AuditEventType.LegalDeadlineDetailsChanged],
            await GetAuditEventTypesAsync(graph.Deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_AppearsInAuditLogWithCodeAndIdentifiersOnly()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage mutation = await SendMutationAsync(
            HttpMethod.Put,
            GetResponsiblePath(graph.Organization.Id, graph.Deadline!.Id),
            graph.RawHandle,
            csrf,
            new { responsibleMembershipId = graph.ResponsibleMembership.Id });
        await AssertEmptyResponseAsync(mutation, HttpStatusCode.NoContent);

        using HttpResponseMessage auditResponse = await SendGetAsync(
            $"/api/organizations/{graph.Organization.Id:D}/audit-logs" +
                "?eventType=legal_deadline.responsible_changed",
            graph.RawHandle);

        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        string auditJson = await auditResponse.Content.ReadAsStringAsync();
        using JsonDocument auditDocument = JsonDocument.Parse(auditJson);
        JsonElement item = Assert.Single(auditDocument.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray());
        Assert.Equal(
            "legal_deadline.responsible_changed",
            item.GetProperty("eventType").GetString());
        Assert.Equal("legal_deadline", item.GetProperty("entityType").GetString());
        Assert.Equal(graph.Deadline.Id, item.GetProperty("entityId").GetGuid());
        Assert.Equal(graph.Membership.Id, item.GetProperty("actorMembershipId").GetGuid());
        JsonElement details = item.GetProperty("details");
        Assert.Equal(
            "legal_deadline.responsible_changed",
            details.GetProperty("type").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            details.GetProperty("oldResponsibleMembershipId").ValueKind);
        Assert.Equal(
            graph.ResponsibleMembership.Id,
            details.GetProperty("newResponsibleMembershipId").GetGuid());
        Assert.Equal(
            ["type", "oldResponsibleMembershipId", "newResponsibleMembershipId"],
            details.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain(graph.Deadline.Title, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Responsible Member", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLegalDeadlines_ResponsiblePair_IsFilledNullOrInactiveWithoutCrossTenantLeak()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Member,
            seedDeadline: true,
            deadlineResponsible: true,
            marker: "read");
        TestGraph foreign = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            deadlineResponsible: true,
            marker: "read");
        LegalDeadline assigned = Assert.IsType<LegalDeadline>(graph.Deadline);
        LegalDeadline unassigned = await SeedDeadlineAsync(
            graph,
            "Read Unassigned",
            1,
            null);
        (User inactiveMembershipUser, OrganizationMembership inactiveMembership) =
            await SeedMemberAsync(
                graph.Organization,
                "read-inactive-membership",
                isMembershipActive: false);
        (User inactiveUser, OrganizationMembership inactiveUserMembership) =
            await SeedMemberAsync(
                graph.Organization,
                "read-inactive-user",
                isUserActive: false);
        LegalDeadline inactiveMembershipDeadline = await SeedDeadlineAsync(
            graph,
            "Read Inactive Membership",
            2,
            inactiveMembership.Id);
        LegalDeadline inactiveUserDeadline = await SeedDeadlineAsync(
            graph,
            "Read Inactive User",
            3,
            inactiveUserMembership.Id);
        Assert.Equal(graph.ResponsibleUser.Name, foreign.ResponsibleUser.Name);

        (Guid Id, Guid? MembershipId, string? DisplayName)[] expected =
        [
            (assigned.Id, graph.ResponsibleMembership.Id, graph.ResponsibleUser.Name),
            (unassigned.Id, null, null),
            (
                inactiveMembershipDeadline.Id,
                inactiveMembership.Id,
                inactiveMembershipUser.Name
            ),
            (inactiveUserDeadline.Id, inactiveUserMembership.Id, inactiveUser.Name)
        ];

        using HttpResponseMessage listResponse = await SendGetAsync(
            GetDeadlinesPath(graph.Organization.Id),
            graph.RawHandle);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        AssertNoForeignIdentifiers(listJson, foreign);
        using JsonDocument listDocument = JsonDocument.Parse(listJson);
        Assert.Equal(
            expected,
            listDocument.RootElement
                .GetProperty("items")
                .EnumerateArray()
                .Select(ReadResponsiblePair)
                .ToArray());

        foreach ((Guid Id, Guid? MembershipId, string? DisplayName) item in expected)
        {
            using HttpResponseMessage getResponse = await SendGetAsync(
                GetDeadlinePath(graph.Organization.Id, item.Id),
                graph.RawHandle);

            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            Assert.True(getResponse.Headers.CacheControl?.NoStore);
            string getJson = await getResponse.Content.ReadAsStringAsync();
            AssertNoForeignIdentifiers(getJson, foreign);
            using JsonDocument getDocument = JsonDocument.Parse(getJson);
            Assert.Equal(item, ReadResponsiblePair(getDocument.RootElement));
        }

        using HttpResponseMessage crossTenantGet = await SendGetAsync(
            GetDeadlinePath(
                graph.Organization.Id,
                Assert.IsType<LegalDeadline>(foreign.Deadline).Id),
            graph.RawHandle);
        await AssertEmptyResponseAsync(crossTenantGet, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListLegalDeadlines_ResponsibleFilter_AppliesWithinTenantAndResolvesSelfOnServer()
    {
        TestGraph graph = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            deadlineResponsible: true,
            marker: "filter");
        TestGraph foreign = await SeedGraphAsync(
            OrganizationRole.Owner,
            seedDeadline: true,
            deadlineResponsible: true,
            marker: "filter");
        LegalDeadline memberDeadline = Assert.IsType<LegalDeadline>(graph.Deadline);
        LegalDeadline ownerDeadline = await SeedDeadlineAsync(
            graph,
            "Filter Owner",
            1,
            graph.Membership.Id);
        LegalDeadline unassigned = await SeedDeadlineAsync(
            graph,
            "Filter Unassigned",
            2,
            null);
        LegalDeadline completedMemberDeadline = await SeedDeadlineAsync(
            graph,
            "Filter Completed",
            3,
            graph.ResponsibleMembership.Id,
            completed: true);
        LegalDeadline foreignOwnerDeadline = await SeedDeadlineAsync(
            foreign,
            "Filter Owner",
            1,
            foreign.Membership.Id);
        string memberHandle = await CreateSessionAsync(graph.ResponsibleUser);

        async Task<Guid[]> ListIdsAsync(
            TestGraph tenant,
            string rawHandle,
            string query,
            TestGraph otherTenant)
        {
            using HttpResponseMessage response = await SendGetAsync(
                $"{GetDeadlinesPath(tenant.Organization.Id)}?{query}",
                rawHandle);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            string json = await response.Content.ReadAsStringAsync();
            AssertNoForeignIdentifiers(json, otherTenant);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal(
                ["items", "pageNumber", "pageSize"],
                document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray());
            return document.RootElement
                .GetProperty("items")
                .EnumerateArray()
                .Select(item => ReadResponsiblePair(item).Id)
                .ToArray();
        }

        Task<Guid[]> OwnerListIdsAsync(string query) =>
            ListIdsAsync(graph, graph.RawHandle, query, foreign);

        Task<Guid[]> MemberListIdsAsync(string query) =>
            ListIdsAsync(graph, memberHandle, query, foreign);

        Guid[] all =
        [
            memberDeadline.Id,
            ownerDeadline.Id,
            unassigned.Id,
            completedMemberDeadline.Id
        ];

        Assert.Equal(all, await OwnerListIdsAsync(string.Empty));
        Assert.Equal(all, await OwnerListIdsAsync("responsible=any"));
        Assert.Equal([ownerDeadline.Id], await OwnerListIdsAsync("responsible=self"));
        Assert.Equal([unassigned.Id], await OwnerListIdsAsync("responsible=unassigned"));
        Assert.Equal(
            [memberDeadline.Id, completedMemberDeadline.Id],
            await OwnerListIdsAsync($"responsible={graph.ResponsibleMembership.Id:D}"));
        Assert.Equal(
            [ownerDeadline.Id],
            await OwnerListIdsAsync($"responsible={graph.Membership.Id:D}"));
        Assert.Empty(await OwnerListIdsAsync(
            $"responsible={foreign.ResponsibleMembership.Id:D}"));
        Assert.Empty(await OwnerListIdsAsync($"responsible={foreign.Membership.Id:D}"));
        Assert.Empty(await OwnerListIdsAsync($"responsible={Guid.NewGuid():D}"));
        Assert.Equal(
            [completedMemberDeadline.Id],
            await OwnerListIdsAsync(
                $"responsible={graph.ResponsibleMembership.Id:D}&pageNumber=2&pageSize=1"));

        Assert.Equal(
            [memberDeadline.Id, completedMemberDeadline.Id],
            await MemberListIdsAsync("responsible=self"));
        Assert.Equal(all, await MemberListIdsAsync("responsible=any"));
        Assert.Equal([unassigned.Id], await MemberListIdsAsync("responsible=unassigned"));
        Assert.Equal(
            [ownerDeadline.Id],
            await MemberListIdsAsync($"responsible={graph.Membership.Id:D}"));

        Assert.Equal(
            [foreignOwnerDeadline.Id],
            await ListIdsAsync(foreign, foreign.RawHandle, "responsible=self", graph));

        string[] invalidQueries =
        [
            $"responsible={Guid.Empty:D}",
            "responsible=not-a-guid",
            $"responsible={graph.ResponsibleMembership.Id:N}",
            "responsible=assigned"
        ];

        foreach (string rawHandle in new[] { graph.RawHandle, memberHandle })
        {
            foreach (string query in invalidQueries)
            {
                using HttpResponseMessage response = await SendGetAsync(
                    $"{GetDeadlinesPath(graph.Organization.Id)}?{query}",
                    rawHandle);
                ProblemDetails problem = await AssertSafeBadRequestAsync(response);
                Assert.Equal("Invalid request data", problem.Title);
            }
        }
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole,
        bool seedDeadline = false,
        bool completedDeadline = false,
        bool deadlineResponsible = false,
        string marker = "primary")
    {
        User user = CreateUser($"{marker}-actor");
        User responsibleUser = CreateUser($"{marker}-responsible");
        var organization = new Organization(
            $"Deadline Responsible {marker} Legal",
            $"deadline-responsible-{marker}-{Guid.NewGuid():N}",
            Now.AddHours(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            actorRole,
            Now.AddHours(-1));
        var responsibleMembership = new OrganizationMembership(
            organization.Id,
            responsibleUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        var relatedClient = new ClientEntity(
            organization.Id,
            $"Deadline Responsible {marker} Client",
            Now.AddMinutes(-30));
        var process = new LegalProcess(
            organization.Id,
            relatedClient.Id,
            $"Deadline Responsible {marker} Process",
            Now.AddMinutes(-20));
        LegalDeadline? deadline = null;

        if (seedDeadline)
        {
            deadline = new LegalDeadline(
                organization.Id,
                process.Id,
                $"Deadline Responsible {marker} Deadline",
                DueDate,
                Now.AddMinutes(-10),
                deadlineResponsible ? responsibleMembership.Id : null);

            if (completedDeadline)
            {
                deadline.Complete(Now.AddMinutes(-5));
            }
        }

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
        dbContext.AddRange(
            organization,
            user,
            responsibleUser,
            credential,
            membership,
            responsibleMembership,
            relatedClient,
            process,
            session);

        if (deadline is not null)
        {
            dbContext.LegalDeadlines.Add(deadline);
        }

        await dbContext.SaveChangesAsync();

        return new TestGraph(
            rawHandle,
            organization,
            membership,
            responsibleUser,
            responsibleMembership,
            process,
            deadline);
    }

    private async Task<Guid[]> PrepareUnavailableMembershipsAsync(TestGraph graph)
    {
        User inactiveMembershipUser = CreateUser("inactive-membership");
        User inactiveUser = CreateUser("inactive-user");
        User foreignUser = CreateUser("foreign-member");
        var foreignOrganization = new Organization(
            "Deadline Responsible Foreign Legal",
            $"deadline-responsible-foreign-{Guid.NewGuid():N}",
            Now.AddHours(-2));
        var inactiveMembership = new OrganizationMembership(
            graph.Organization.Id,
            inactiveMembershipUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        var inactiveUserMembership = new OrganizationMembership(
            graph.Organization.Id,
            inactiveUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        inactiveMembership.Deactivate();
        inactiveUser.Deactivate();

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            inactiveMembershipUser,
            inactiveUser,
            foreignUser,
            foreignOrganization,
            inactiveMembership,
            inactiveUserMembership,
            foreignMembership);
        await dbContext.SaveChangesAsync();

        return
        [
            foreignMembership.Id,
            Guid.NewGuid(),
            inactiveMembership.Id,
            inactiveUserMembership.Id
        ];
    }

    private async Task ApplyResponsibleStateAsync(
        TestGraph graph,
        ResponsibleState state)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        if (state == ResponsibleState.InactiveMembership)
        {
            OrganizationMembership membership = await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleMembership.Id);
            membership.Deactivate();
        }
        else if (state == ResponsibleState.InactiveUser)
        {
            User user = await dbContext.Users
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleUser.Id);
            user.Deactivate();
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalDeadline> SeedDeadlineAsync(
        TestGraph graph,
        string title,
        int dueDateOffsetDays,
        Guid? responsibleMembershipId,
        bool completed = false)
    {
        var deadline = new LegalDeadline(
            graph.Organization.Id,
            graph.Process.Id,
            title,
            DueDate.AddDays(dueDateOffsetDays),
            Now.AddMinutes(-10),
            responsibleMembershipId);

        if (completed)
        {
            deadline.Complete(Now.AddMinutes(-5));
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalDeadlines.Add(deadline);
        await dbContext.SaveChangesAsync();
        return deadline;
    }

    private async Task<(User User, OrganizationMembership Membership)> SeedMemberAsync(
        Organization organization,
        string marker,
        bool isMembershipActive = true,
        bool isUserActive = true)
    {
        User user = CreateUser(marker);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));

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
        return (user, membership);
    }

    private async Task<string> CreateSessionAsync(User user)
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
        dbContext.AddRange(credential, session);
        await dbContext.SaveChangesAsync();
        return rawHandle;
    }

    private static (Guid Id, Guid? MembershipId, string? DisplayName) ReadResponsiblePair(
        JsonElement item)
    {
        JsonElement membershipId = item.GetProperty("responsibleMembershipId");
        JsonElement displayName = item.GetProperty("responsibleDisplayName");
        bool membershipIsNull = membershipId.ValueKind == JsonValueKind.Null;

        Assert.Equal(
            membershipIsNull,
            displayName.ValueKind == JsonValueKind.Null);

        return (
            item.GetProperty("id").GetGuid(),
            membershipIsNull ? null : membershipId.GetGuid(),
            membershipIsNull ? null : displayName.GetString());
    }

    private static void AssertNoForeignIdentifiers(string json, TestGraph foreign)
    {
        Guid[] foreignIds =
        [
            foreign.Organization.Id,
            foreign.Membership.Id,
            foreign.ResponsibleMembership.Id,
            foreign.Process.Id,
            Assert.IsType<LegalDeadline>(foreign.Deadline).Id
        ];

        foreach (Guid foreignId in foreignIds)
        {
            Assert.DoesNotContain(
                foreignId.ToString(),
                json,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task AssertNoDeadlineWritesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalDeadlines.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    private async Task<AuditEventType[]> GetAuditEventTypesAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .Where(auditLog => auditLog.EntityId == deadlineId)
            .OrderBy(auditLog => auditLog.OccurredAt)
            .Select(auditLog => auditLog.EventType)
            .ToArrayAsync();
    }

    private async Task<LegalDeadline> GetPersistedDeadlineAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineId);
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Deadline Responsible {marker}",
            $"deadline-responsible-{marker}-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        return user;
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
        Assert.DoesNotContain("membershipId", responseContent, StringComparison.OrdinalIgnoreCase);
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

    private static string GetResponsiblePath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinePath(organizationId, deadlineId)}/responsible";
    }

    private static string GetCompletePath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinePath(organizationId, deadlineId)}/complete";
    }

    private static string GetReopenPath(Guid organizationId, Guid deadlineId)
    {
        return $"{GetDeadlinePath(organizationId, deadlineId)}/reopen";
    }

    public enum ResponsibleState
    {
        None = 0,
        Active = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    private sealed record TestGraph(
        string RawHandle,
        Organization Organization,
        OrganizationMembership Membership,
        User ResponsibleUser,
        OrganizationMembership ResponsibleMembership,
        LegalProcess Process,
        LegalDeadline? Deadline);

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
