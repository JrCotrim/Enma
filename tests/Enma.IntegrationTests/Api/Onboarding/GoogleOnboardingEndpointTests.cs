using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Onboarding;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleOnboardingEndpointTests : IAsyncLifetime
{
    private const string ExternalScheme = "EnmaExternal";
    private const string ExternalCookieName = "__Host-enma_google_external";
    private const string SessionCookieName = "__Host-enma_session";
    private const string InvitationItem = "enma:invitation";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public GoogleOnboardingEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task CompleteProfileThenInitialOrganization_CreatesGoogleOnlyOwnerTenant()
    {
        string externalCookie = CreateExternalCookie(
            "new-google-subject",
            "new-google@example.test");
        CsrfToken csrf = await GetCsrfAsync(externalCookie);
        using var profileRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/auth/google/complete-profile")
        {
            Content = JsonContent.Create(new { name = "Google User" })
        };
        AddSecurityHeaders(profileRequest, [externalCookie, csrf.Cookie], csrf.Token);

        using HttpResponseMessage profileResponse = await client.SendAsync(
            profileRequest);

        Assert.Equal(HttpStatusCode.NoContent, profileResponse.StatusCode);
        string sessionCookie = GetResponseCookie(
            profileResponse,
            SessionCookieName);
        CsrfToken authenticatedCsrf = await GetCsrfAsync(
            externalCookie,
            sessionCookie);
        using var organizationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/onboarding/initial-organization")
        {
            Content = JsonContent.Create(new
            {
                organizationName = "Google Firm",
                organizationSlug = "google-firm"
            })
        };
        AddSecurityHeaders(
            organizationRequest,
            [externalCookie, sessionCookie, authenticatedCsrf.Cookie],
            authenticatedCsrf.Token);

        using HttpResponseMessage organizationResponse = await client.SendAsync(
            organizationRequest);

        Assert.Equal(HttpStatusCode.Created, organizationResponse.StatusCode);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal("new-google@example.test", user.Email);
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Null((await dbContext.UserCredentials.AsNoTracking().SingleAsync())
            .PasswordHash);
        Assert.Equal(
            user.Id,
            (await dbContext.ExternalIdentities.AsNoTracking().SingleAsync()).UserId);
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.Single(await dbContext.Organizations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InitialOrganization_InvitationHandoff_IsRejectedBeforeCreation()
    {
        SeededGoogleUser seeded = await SeedGoogleUserAsync();
        string externalCookie = CreateExternalCookie(
            seeded.Subject,
            seeded.Email,
            invitationToken: "protected-invitation-capability");
        CsrfToken csrf = await GetCsrfAsync(externalCookie, seeded.SessionCookie);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/onboarding/initial-organization")
        {
            Content = JsonContent.Create(new
            {
                organizationName = "Must Not Exist",
                organizationSlug = "must-not-exist"
            })
        };
        AddSecurityHeaders(
            request,
            [externalCookie, seeded.SessionCookie, csrf.Cookie],
            csrf.Token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.OrganizationMemberships
            .AsNoTracking()
            .ToListAsync());
    }

    [Fact]
    public async Task InitialOrganization_ExistingSlug_ReturnsSpecificConflict()
    {
        SeededGoogleUser seeded = await SeedGoogleUserAsync();
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.Organizations.Add(new Organization(
                "Existing Firm",
                "existing-firm",
                DateTimeOffset.UtcNow));
            await seedContext.SaveChangesAsync();
        }
        string externalCookie = CreateExternalCookie(seeded.Subject, seeded.Email);
        CsrfToken csrf = await GetCsrfAsync(externalCookie, seeded.SessionCookie);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/onboarding/initial-organization")
        {
            Content = JsonContent.Create(new
            {
                organizationName = "New Firm",
                organizationSlug = "existing-firm"
            })
        };
        AddSecurityHeaders(
            request,
            [externalCookie, seeded.SessionCookie, csrf.Cookie],
            csrf.Token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "organization_slug_conflict",
            problem.RootElement.GetProperty("code").GetString());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.OrganizationMemberships
            .AsNoTracking()
            .ToListAsync());
    }

    private async Task<SeededGoogleUser> SeedGoogleUserAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string email = "invited-google@example.test";
        const string subject = "invited-google-subject";
        var user = new User("Invited Google", email, now.AddMinutes(-2));
        user.VerifyEmail(now.AddMinutes(-2));
        var credential = new UserCredential(user.Id, passwordHash: null, now);
        var identity = new ExternalIdentity(user.Id, "Google", subject, now);
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            now,
            now.AddMinutes(30),
            now.AddHours(1));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(user, credential, identity, session);
        await dbContext.SaveChangesAsync();
        return new(email, subject, $"{SessionCookieName}={rawHandle}");
    }

    private string CreateExternalCookie(
        string subject,
        string email,
        string? invitationToken = null)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("email", email),
                new Claim("email_verified", "true")
            ],
            ExternalScheme);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        if (invitationToken is not null)
        {
            properties.Items[InvitationItem] = invitationToken;
        }

        CookieAuthenticationOptions options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ExternalScheme);
        string value = options.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            properties,
            ExternalScheme));
        return $"{ExternalCookieName}={value}";
    }

    private async Task<CsrfToken> GetCsrfAsync(params string[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        if (cookies.Length > 0)
        {
            request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        CsrfResponse body = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        string cookie = response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => value.Split(';', 2)[0])
            .Single(value => !value.StartsWith(ExternalCookieName, StringComparison.Ordinal));
        return new(cookie, body.RequestToken);
    }

    private static string GetResponseCookie(
        HttpResponseMessage response,
        string cookieName)
    {
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(
                $"{cookieName}=",
                StringComparison.Ordinal));
    }

    private static void AddSecurityHeaders(
        HttpRequestMessage request,
        IReadOnlyCollection<string> cookies,
        string csrfToken)
    {
        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        request.Headers.Add(CsrfHeaderName, csrfToken);
    }

    private sealed record CsrfResponse(string RequestToken);
    private sealed record CsrfToken(string Cookie, string Token);
    private sealed record SeededGoogleUser(
        string Email,
        string Subject,
        string SessionCookie);
}
