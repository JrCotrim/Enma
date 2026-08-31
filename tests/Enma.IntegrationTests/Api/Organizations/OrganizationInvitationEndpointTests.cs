using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Auditing;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationInvitationEndpointTests : IAsyncLifetime
{
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string CsrfPath = "/api/auth/csrf";
    private const string PasswordHash = "synthetic-invitation-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        30,
        14,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly CapturingDelivery delivery = new();
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationInvitationEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.RemoveAll<IOrganizationInvitationDelivery>();
            services.AddSingleton<IOrganizationInvitationDelivery>(delivery);
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
    public void Contracts_ExposeOnlyAdministrativeFields()
    {
        Assert.Equal(
            [nameof(CreateOrganizationInvitationRequest.Email),
                nameof(CreateOrganizationInvitationRequest.Role)],
            typeof(CreateOrganizationInvitationRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray());
        Assert.DoesNotContain(
            typeof(OrganizationInvitationResponse).GetProperties(),
            property => property.Name.Contains("Token", StringComparison.Ordinal) ||
                property.Name.Contains("AcceptedBy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Endpoints_Anonymous_ReturnEmptyNoStoreUnauthorizedBeforeCsrf()
    {
        Guid organizationId = Guid.NewGuid();
        Guid invitationId = Guid.NewGuid();

        using HttpResponseMessage list = await client.GetAsync(
            InvitationsPath(organizationId));
        using HttpResponseMessage revoke = await client.PostAsync(
            RevokePath(organizationId, invitationId),
            content: null);

        await AssertEmptyResponseAsync(list, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(revoke, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_OwnerAdministratorInvite_CommitsAuditThenReportsAccepted()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendCreateAsync(
            graph,
            csrf,
            " New.Admin@Example.Test ",
            "Administrator");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        OrganizationInvitationMutationResponse body = Assert.IsType<
            OrganizationInvitationMutationResponse>(
                await response.Content.ReadFromJsonAsync<
                    OrganizationInvitationMutationResponse>());
        Assert.Equal("accepted", body.DeliveryStatus);
        string rawResponse = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", rawResponse, StringComparison.OrdinalIgnoreCase);
        OrganizationInvitationDeliveryRequest deliveryRequest = Assert.Single(
            delivery.Requests);
        Assert.Equal("new.admin@example.test", deliveryRequest.Email);
        Assert.Equal(graph.Organization.Name, deliveryRequest.OrganizationName);
        Assert.Equal(OrganizationRole.Administrator, deliveryRequest.Role);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation invitation = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        AuditLog audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal(body.InvitationId, invitation.Id);
        Assert.Equal(AuditEventType.OrganizationInvitationCreated, audit.EventType);
        Assert.Equal(invitation.Id, audit.EntityId);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator, "Member", HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Administrator, "Administrator", HttpStatusCode.Forbidden)]
    [InlineData(OrganizationRole.Member, "Member", HttpStatusCode.Forbidden)]
    public async Task Create_EnforcesLiveRoleMatrix(
        OrganizationRole actorRole,
        string invitedRole,
        HttpStatusCode expectedStatus)
    {
        TestGraph graph = await SeedGraphAsync(actorRole);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendCreateAsync(
            graph,
            csrf,
            "matrix@example.test",
            invitedRole);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            expectedStatus == HttpStatusCode.Created ? 1 : 0,
            await dbContext.OrganizationInvitations.CountAsync());
    }

    [Fact]
    public async Task Create_OwnerRoleAndInvalidEmail_ReturnSafeNoStoreBadRequest()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage ownerRole = await SendCreateAsync(
            graph,
            csrf,
            "owner@example.test",
            "Owner");
        using HttpResponseMessage invalidEmail = await SendCreateAsync(
            graph,
            csrf,
            "not-an-email",
            "Member");

        await AssertProblemResponseAsync(ownerRole, HttpStatusCode.BadRequest);
        await AssertProblemResponseAsync(invalidEmail, HttpStatusCode.BadRequest);
        Assert.Empty(delivery.Requests);
    }

    [Fact]
    public async Task Mutations_MissingAntiforgery_ReturnNoStoreBadRequestWithoutWrites()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Member);

        using HttpResponseMessage create = await SendCreateAsync(
            graph,
            csrf: null,
            "csrf@example.test",
            "Member");
        using HttpResponseMessage revoke = await SendMutationAsync(
            RevokePath(graph.Organization.Id, invitation.Id),
            graph.RawHandle,
            csrf: null);
        using HttpResponseMessage resend = await SendMutationAsync(
            ResendPath(graph.Organization.Id, invitation.Id),
            graph.RawHandle,
            csrf: null);

        await AssertEmptyResponseAsync(create, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(revoke, HttpStatusCode.BadRequest);
        await AssertEmptyResponseAsync(resend, HttpStatusCode.BadRequest);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_DuplicatePending_ReturnsSafeConflictWithoutSecondDelivery()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Member,
            "duplicate@example.test");
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendCreateAsync(
            graph,
            csrf,
            invitation.InvitedEmail,
            "Member");

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        Assert.Empty(delivery.Requests);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Revoke_ForeignInvitation_MatchesMissingNotFound()
    {
        TestGraph current = await SeedGraphAsync(
            OrganizationRole.Owner,
            "current");
        TestGraph foreign = await SeedGraphAsync(
            OrganizationRole.Owner,
            "foreign");
        OrganizationInvitation foreignInvitation = await SeedInvitationAsync(
            foreign,
            OrganizationRole.Member);
        CsrfPair csrf = await GetCsrfPairAsync(current.RawHandle);

        using HttpResponseMessage foreignResponse = await SendMutationAsync(
            RevokePath(current.Organization.Id, foreignInvitation.Id),
            current.RawHandle,
            csrf);
        using HttpResponseMessage missingResponse = await SendMutationAsync(
            RevokePath(current.Organization.Id, Guid.NewGuid()),
            current.RawHandle,
            csrf);

        await AssertEmptyResponseAsync(foreignResponse, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(missingResponse, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resend_RotatesThenCooldownsWithoutReturningToken()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Administrator);
        OrganizationInvitationTokenHash oldHash = invitation.TokenHash!;
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage first = await SendMutationAsync(
            ResendPath(graph.Organization.Id, invitation.Id),
            graph.RawHandle,
            csrf);
        using HttpResponseMessage second = await SendMutationAsync(
            ResendPath(graph.Organization.Id, invitation.Id),
            graph.RawHandle,
            csrf);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        OrganizationInvitationMutationResponse body = Assert.IsType<
            OrganizationInvitationMutationResponse>(
                await first.Content.ReadFromJsonAsync<
                    OrganizationInvitationMutationResponse>());
        Assert.Equal("accepted", body.DeliveryStatus);
        Assert.DoesNotContain(
            "token",
            await first.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        await AssertProblemResponseAsync(
            second,
            HttpStatusCode.TooManyRequests);
        Assert.Equal("60", second.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Single(delivery.Requests);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.NotEqual(oldHash, stored.TokenHash);
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync(audit =>
            audit.EventType == AuditEventType.OrganizationInvitationResent));
    }

    [Fact]
    public async Task Revoke_ExpiredMaterializesAndReturnsConflictWithoutAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var invitation = new OrganizationInvitation(
            graph.Organization.Id,
            "expired-http@example.test",
            OrganizationRole.Member,
            graph.Membership.Id,
            RandomHash(),
            Now.AddDays(-8),
            Now.AddDays(-8),
            Now);
        await SeedAsync(invitation);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            RevokePath(graph.Organization.Id, invitation.Id),
            graph.RawHandle,
            csrf);

        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(invitation.ExpiresAt, stored.ExpiredAt);
        Assert.Null(stored.TokenHash);
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Mutations_AcceptedAndRevokedInvitations_ReturnConflict()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var acceptedUser = new User(
            "Accepted User",
            "accepted-user@example.test",
            Now.AddHours(-2));
        var accepted = new OrganizationInvitation(
            graph.Organization.Id,
            acceptedUser.Email,
            OrganizationRole.Member,
            graph.Membership.Id,
            RandomHash(),
            Now.AddHours(-1),
            Now.AddMinutes(-2),
            Now.AddDays(7));
        accepted.Accept(acceptedUser.Id, Now.AddMinutes(-1));
        var revoked = new OrganizationInvitation(
            graph.Organization.Id,
            "revoked-user@example.test",
            OrganizationRole.Member,
            graph.Membership.Id,
            RandomHash(),
            Now.AddHours(-1),
            Now.AddMinutes(-2),
            Now.AddDays(7));
        revoked.Revoke(Now.AddMinutes(-1));
        await SeedAsync(acceptedUser, accepted, revoked);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage revokeAccepted = await SendMutationAsync(
            RevokePath(graph.Organization.Id, accepted.Id),
            graph.RawHandle,
            csrf);
        using HttpResponseMessage resendRevoked = await SendMutationAsync(
            ResendPath(graph.Organization.Id, revoked.Id),
            graph.RawHandle,
            csrf);

        await AssertProblemResponseAsync(
            revokeAccepted,
            HttpStatusCode.Conflict);
        await AssertProblemResponseAsync(
            resendRevoked,
            HttpStatusCode.Conflict);
        Assert.Empty(delivery.Requests);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_DeliveryFailure_StillReturnsCommittedInvitationSafely()
    {
        delivery.Result = OrganizationInvitationDeliveryResult.Failed;
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);

        using HttpResponseMessage response = await SendCreateAsync(
            graph,
            csrf,
            "smtp-failure@example.test",
            "Member");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        OrganizationInvitationMutationResponse body = Assert.IsType<
            OrganizationInvitationMutationResponse>(
                await response.Content.ReadFromJsonAsync<
                    OrganizationInvitationMutationResponse>());
        Assert.Equal("failed", body.DeliveryStatus);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SMTP", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_SendRateLimit_RejectsSixthRequestNoStore()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(graph.RawHandle);
        var responses = new List<HttpResponseMessage>();

        try
        {
            for (int index = 0; index < 6; index++)
            {
                responses.Add(await SendCreateAsync(
                    graph,
                    csrf,
                    $"rate-{index}@example.test",
                    "Owner"));
            }

            Assert.All(
                responses.Take(5),
                response => Assert.Equal(
                    HttpStatusCode.BadRequest,
                    response.StatusCode));
            HttpResponseMessage rejected = responses[5];
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.True(rejected.Headers.CacheControl?.NoStore);
            Assert.Empty(delivery.Requests);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task List_IsAdminOnlyTenantQualifiedBoundedAndNeverExposesToken()
    {
        TestGraph owner = await SeedGraphAsync(OrganizationRole.Owner, "owner");
        TestGraph foreign = await SeedGraphAsync(OrganizationRole.Owner, "foreign");
        TestGraph member = await SeedGraphAsync(OrganizationRole.Member, "member");
        var tokenService = new CryptographicOrganizationInvitationTokenService();
        string rawToken = tokenService.GenerateToken(out var hash);
        OrganizationInvitation currentInvitation = await SeedInvitationAsync(
            owner,
            OrganizationRole.Member,
            "same@example.test",
            hash);
        await SeedInvitationAsync(
            foreign,
            OrganizationRole.Member,
            "same@example.test");

        using HttpResponseMessage ownerResponse = await SendGetAsync(
            $"{InvitationsPath(owner.Organization.Id)}?pageNumber=1&pageSize=1",
            owner.RawHandle);
        using HttpResponseMessage memberResponse = await SendGetAsync(
            InvitationsPath(member.Organization.Id),
            member.RawHandle);
        using HttpResponseMessage unboundedResponse = await SendGetAsync(
            $"{InvitationsPath(owner.Organization.Id)}?pageSize=101",
            owner.RawHandle);

        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.True(ownerResponse.Headers.CacheControl?.NoStore);
        ListOrganizationInvitationsResponse body = Assert.IsType<
            ListOrganizationInvitationsResponse>(
                await ownerResponse.Content.ReadFromJsonAsync<
                    ListOrganizationInvitationsResponse>());
        Assert.Equal(1, body.TotalCount);
        OrganizationInvitationResponse item = Assert.Single(body.Items);
        Assert.Equal(currentInvitation.Id, item.Id);
        string rawBody = await ownerResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawToken, rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", rawBody, StringComparison.OrdinalIgnoreCase);
        await AssertEmptyResponseAsync(memberResponse, HttpStatusCode.Forbidden);
        await AssertProblemResponseAsync(
            unboundedResponse,
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_StaleInactiveStatesFailClosed()
    {
        TestGraph demoted = await SeedGraphAsync(
            OrganizationRole.Owner,
            "demoted");
        TestGraph inactiveMembership = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-membership");
        TestGraph inactiveOrganization = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-organization");
        TestGraph inactiveUser = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-user");
        await MutateGraphAsync(demoted, membership =>
            membership.ChangeRole(OrganizationRole.Member));
        await MutateGraphAsync(inactiveMembership, membership =>
            membership.Deactivate());
        await MutateOrganizationAsync(inactiveOrganization.Organization.Id);
        await MutateUserAsync(inactiveUser.Actor.Id);
        CsrfPair demotedCsrf = await GetCsrfPairAsync(demoted.RawHandle);

        using HttpResponseMessage demotedResponse = await SendCreateAsync(
            demoted,
            demotedCsrf,
            "demoted@example.test",
            "Member");
        using HttpResponseMessage inactiveMembershipResponse =
            await SendCreateWithoutCsrfAsync(inactiveMembership);
        using HttpResponseMessage inactiveOrganizationResponse =
            await SendCreateWithoutCsrfAsync(inactiveOrganization);
        using HttpResponseMessage inactiveUserResponse =
            await SendCreateWithoutCsrfAsync(inactiveUser);

        await AssertEmptyResponseAsync(demotedResponse, HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            inactiveMembershipResponse,
            HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            inactiveOrganizationResponse,
            HttpStatusCode.Forbidden);
        await AssertEmptyResponseAsync(
            inactiveUserResponse,
            HttpStatusCode.Unauthorized);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.OrganizationInvitations.CountAsync());
    }

    private async Task<HttpResponseMessage> SendCreateWithoutCsrfAsync(
        TestGraph graph)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            InvitationsPath(graph.Organization.Id))
        {
            Content = JsonContent.Create(new
            {
                email = $"inactive-{Guid.NewGuid():N}@example.test",
                role = "Member"
            })
        };
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={graph.RawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole role,
        string marker = "default")
    {
        string suffix = $"{marker}-{Guid.NewGuid():N}";
        var organization = new Organization(
            $"{marker} Legal",
            $"invitation-http-{suffix}",
            Now.AddHours(-2));
        var actor = new User(
            $"Actor {marker}",
            $"actor-{suffix}@example.test",
            Now.AddHours(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            role,
            Now.AddHours(-2));
        AuthenticatedSession authenticated = CreateSession(actor);
        await SeedAsync(
            organization,
            actor,
            membership,
            authenticated.Credential,
            authenticated.Session);
        return new TestGraph(
            organization,
            actor,
            membership,
            authenticated.RawHandle);
    }

    private AuthenticatedSession CreateSession(User user)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-2));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));
        return new AuthenticatedSession(rawHandle, credential, session);
    }

    private async Task<OrganizationInvitation> SeedInvitationAsync(
        TestGraph graph,
        OrganizationRole role,
        string? email = null,
        OrganizationInvitationTokenHash? tokenHash = null)
    {
        var invitation = new OrganizationInvitation(
            graph.Organization.Id,
            email ?? $"invited-{Guid.NewGuid():N}@example.test",
            role,
            graph.Membership.Id,
            tokenHash ?? RandomHash(),
            Now.AddHours(-1),
            Now.AddMinutes(-2),
            Now.AddDays(7));
        await SeedAsync(invitation);
        return invitation;
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse body = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
        return new CsrfPair(body.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendCreateAsync(
        TestGraph graph,
        CsrfPair? csrf,
        string email,
        string role)
    {
        using HttpRequestMessage request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            InvitationsPath(graph.Organization.Id),
            graph.RawHandle,
            csrf);
        request.Content = JsonContent.Create(new { email, role });
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(
        string path,
        string rawHandle,
        CsrfPair? csrf)
    {
        using HttpRequestMessage request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            path,
            rawHandle,
            csrf);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string path,
        string rawHandle,
        CsrfPair? csrf)
    {
        var request = new HttpRequestMessage(method, path);
        var cookies = new List<string> { $"{SessionCookieName}={rawHandle}" };

        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
            request.Headers.Add(CsrfHeaderName, csrf.RequestToken);
        }

        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        return request;
    }

    private async Task MutateGraphAsync(
        TestGraph graph,
        Action<OrganizationMembership> mutation)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == graph.Membership.Id);
        mutation(membership);
        await dbContext.SaveChangesAsync();
    }

    private async Task MutateOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations.SingleAsync(
            candidate => candidate.Id == organizationId);
        organization.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task MutateUserAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.SingleAsync(candidate =>
            candidate.Id == userId);
        user.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static OrganizationInvitationTokenHash RandomHash()
    {
        return new OrganizationInvitationTokenHash(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    private static string InvitationsPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/invitations";
    }

    private static string RevokePath(Guid organizationId, Guid invitationId)
    {
        return $"{InvitationsPath(organizationId)}/{invitationId:D}/revoke";
    }

    private static string ResendPath(Guid organizationId, Guid invitationId)
    {
        return $"{InvitationsPath(organizationId)}/{invitationId:D}/resend";
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

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertProblemResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", content, StringComparison.Ordinal);
        Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SMTP", content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestGraph(
        Organization Organization,
        User Actor,
        OrganizationMembership Membership,
        string RawHandle);

    private sealed record AuthenticatedSession(
        string RawHandle,
        UserCredential Credential,
        AuthenticationSession Session);

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CapturingDelivery : IOrganizationInvitationDelivery
    {
        public OrganizationInvitationDeliveryResult Result { get; set; } =
            OrganizationInvitationDeliveryResult.Accepted;

        public List<OrganizationInvitationDeliveryRequest> Requests { get; } = [];

        public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
            OrganizationInvitationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
