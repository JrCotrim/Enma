using System.Net;
using System.Net.Http.Json;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class LogoutEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string LogoutPath = "/api/auth/logout";
    private const string ProbePath = "/_test/logout-authenticated";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash = "synthetic-logout-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        20,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LogoutEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IStartupFilter, AuthenticationProbeStartupFilter>();
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
    public async Task GetCsrf_AnonymousRequest_ReturnsNoStoreTokenAndSecureAntiforgeryCookie()
    {
        using HttpResponseMessage response = await client.GetAsync(CsrfPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);

        CsrfResponse? result = await response.Content.ReadFromJsonAsync<CsrfResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.RequestToken));

        SetCookieHeaderValue cookie = AssertSingleCookie(
            response,
            AntiforgeryCookieName);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);
        Assert.Null(cookie.MaxAge);
    }

    [Fact]
    public async Task PostLogout_ValidSession_RevokesSessionDeletesCookiesAndRejectsOldHandleReplay()
    {
        SeededSession seeded = await SeedSessionAsync();
        CsrfPair csrf = await GetCsrfPairAsync(seeded.RawHandle);

        using HttpResponseMessage response = await PostLogoutAsync(
            seeded.RawHandle,
            csrf);

        await AssertSuccessfulLogoutAsync(response);
        AssertSessionCookieDeletion(response);
        AssertAntiforgeryCookieDeletion(response);

        AuthenticationSession persisted = await GetSessionAsync(seeded.SessionId);
        Assert.NotNull(persisted.RevokedAt);

        using HttpResponseMessage replayResponse = await GetProbeAsync(
            seeded.RawHandle);

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        Assert.True(replayResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(
            string.Empty,
            await replayResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostLogout_MissingCsrf_RejectsWithoutRevocationOrCookieDeletion()
    {
        SeededSession seeded = await SeedSessionAsync();

        using HttpResponseMessage response = await PostLogoutAsync(
            seeded.RawHandle,
            csrf: null);

        await AssertRejectedLogoutAsync(response);
        AssertNoAuthenticationCookieDeletion(response);
        AuthenticationSession persisted = await GetSessionAsync(seeded.SessionId);
        Assert.Null(persisted.RevokedAt);
    }

    [Fact]
    public async Task PostLogout_InvalidCsrf_RejectsWithoutRevocationOrCookieDeletion()
    {
        SeededSession seeded = await SeedSessionAsync();
        CsrfPair csrf = await GetCsrfPairAsync(seeded.RawHandle);

        using HttpResponseMessage response = await PostLogoutAsync(
            seeded.RawHandle,
            csrf,
            requestTokenOverride: "malformed");

        await AssertRejectedLogoutAsync(response);
        AssertNoAuthenticationCookieDeletion(response);
        AuthenticationSession persisted = await GetSessionAsync(seeded.SessionId);
        Assert.Null(persisted.RevokedAt);
    }

    [Fact]
    public async Task PostLogout_IdentityMismatch_RejectsWithoutRevokingPresentedSession()
    {
        SeededSession userA = await SeedSessionAsync();
        SeededSession userB = await SeedSessionAsync();
        CsrfPair userACsrf = await GetCsrfPairAsync(userA.RawHandle);

        using HttpResponseMessage response = await PostLogoutAsync(
            userB.RawHandle,
            userACsrf);

        await AssertRejectedLogoutAsync(response);
        AssertNoAuthenticationCookieDeletion(response);
        AuthenticationSession persistedUserB = await GetSessionAsync(
            userB.SessionId);
        Assert.Null(persistedUserB.RevokedAt);
    }

    [Fact]
    public async Task PostLogout_IdleExpiredSession_ObtainsAnonymousCsrfAndRevokesSession()
    {
        SeededSession seeded = await SeedSessionAsync(idleExpired: true);

        using HttpResponseMessage unauthenticatedResponse = await GetProbeAsync(
            seeded.RawHandle);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            unauthenticatedResponse.StatusCode);

        CsrfPair csrf = await GetCsrfPairAsync(seeded.RawHandle);
        using HttpResponseMessage response = await PostLogoutAsync(
            seeded.RawHandle,
            csrf);

        await AssertSuccessfulLogoutAsync(response);
        AssertSessionCookieDeletion(response);
        AuthenticationSession persisted = await GetSessionAsync(seeded.SessionId);
        Assert.NotNull(persisted.RevokedAt);
    }

    [Fact]
    public async Task PostLogout_MissingSessionWithValidCsrf_ReturnsGenericSuccessWithoutCreatingSession()
    {
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle: null);

        using HttpResponseMessage response = await PostLogoutAsync(
            rawHandle: null,
            csrf);

        await AssertSuccessfulLogoutAsync(response);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuthenticationSessions.CountAsync());
    }

    private async Task<SeededSession> SeedSessionAsync(bool idleExpired = false)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var user = new User(
            "HTTP Logout User",
            $"http-logout-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            idleExpired ? Now.AddMinutes(-1) : Now.AddMinutes(5),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return new SeededSession(session.Id, rawHandle);
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string? rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);

        if (rawHandle is not null)
        {
            request.Headers.Add(
                HeaderNames.Cookie,
                $"{SessionCookieName}={rawHandle}");
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        CsrfResponse? result = await response.Content.ReadFromJsonAsync<CsrfResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.RequestToken));
        SetCookieHeaderValue cookie = AssertSingleCookie(
            response,
            AntiforgeryCookieName);

        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> PostLogoutAsync(
        string? rawHandle,
        CsrfPair? csrf,
        string? requestTokenOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LogoutPath);
        var cookies = new List<string>();

        if (rawHandle is not null)
        {
            cookies.Add($"{SessionCookieName}={rawHandle}");
        }

        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        if (cookies.Count > 0)
        {
            request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        }

        string? requestToken = requestTokenOverride ?? csrf?.RequestToken;

        if (requestToken is not null)
        {
            request.Headers.Add(CsrfHeaderName, requestToken);
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetProbeAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<AuthenticationSession> GetSessionAsync(Guid sessionId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sessionId);
    }

    private static SetCookieHeaderValue AssertSingleCookie(
        HttpResponseMessage response,
        string cookieName)
    {
        SetCookieHeaderValue[] matches = ParseSetCookies(response)
            .Where(cookie => string.Equals(
                cookie.Name.ToString(),
                cookieName,
                StringComparison.Ordinal))
            .ToArray();

        return Assert.Single(matches);
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

    private static void AssertSessionCookieDeletion(HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = AssertSingleCookie(
            response,
            SessionCookieName);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Lax", cookie.SameSite.ToString());
        Assert.False(cookie.Domain.HasValue);
        AssertDeletionSemantics(cookie);
    }

    private static void AssertAntiforgeryCookieDeletion(
        HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = AssertSingleCookie(
            response,
            AntiforgeryCookieName);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.False(cookie.Domain.HasValue);
        AssertDeletionSemantics(cookie);
    }

    private static void AssertDeletionSemantics(SetCookieHeaderValue cookie)
    {
        bool expiresInPast = cookie.Expires is DateTimeOffset expires &&
            expires <= DateTimeOffset.UnixEpoch;
        bool clearsMaxAge = cookie.MaxAge is TimeSpan maxAge &&
            maxAge <= TimeSpan.Zero;

        Assert.True(expiresInPast || clearsMaxAge);
    }

    private static void AssertNoAuthenticationCookieDeletion(
        HttpResponseMessage response)
    {
        IReadOnlyList<SetCookieHeaderValue> cookies = ParseSetCookies(response);
        Assert.DoesNotContain(
            cookies,
            cookie => string.Equals(
                cookie.Name.ToString(),
                SessionCookieName,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            cookies,
            cookie => string.Equals(
                cookie.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
    }

    private static async Task AssertSuccessfulLogoutAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
    }

    private static async Task AssertRejectedLogoutAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
    }

    private sealed class AuthenticationProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(
            Action<IApplicationBuilder> next)
        {
            return application =>
            {
                next(application);
                application.UseEndpoints(endpoints =>
                {
                    endpoints
                        .MapGet(ProbePath, () => TypedResults.NoContent())
                        .RequireAuthorization();
                });
            };
        }
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

    private sealed record SeededSession(Guid SessionId, string RawHandle);
}
