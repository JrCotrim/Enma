using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
public sealed class SessionAuthenticationPipelineTests : IAsyncLifetime
{
    private const string ProbePath = "/_test/authenticated";
    private const string SessionCookieName = "__Host-enma_session";
    private const string PasswordHash = "synthetic-session-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        18,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public SessionAuthenticationPipelineTests(PostgreSqlFixture fixture)
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
            BaseAddress = new Uri("https://localhost")
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
    public async Task GetProtectedProbe_ValidSession_AuthenticatesWithMinimalPrincipalAndRenewsIdleExpiration()
    {
        SeededSession seeded = await SeedSessionAsync();

        using HttpResponseMessage response = await GetProbeAsync(seeded.RawHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AuthenticationProbeResponse? result =
            await response.Content.ReadFromJsonAsync<AuthenticationProbeResponse>();
        Assert.NotNull(result);
        Assert.True(result.IsAuthenticated);
        Assert.Equal("EnmaSession", result.AuthenticationType);
        ClaimValue claim = Assert.Single(result.Claims);
        Assert.Equal(ClaimTypes.NameIdentifier, claim.Type);
        Assert.Equal(seeded.UserId.ToString("D"), claim.Value);
        Assert.DoesNotContain(
            result.Claims,
            candidate => candidate.Type == ClaimTypes.Role);
        Assert.DoesNotContain(
            result.Claims,
            candidate => candidate.Type.Contains("Organization", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Claims,
            candidate => candidate.Type.Contains("Membership", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Claims,
            candidate => candidate.Type.Contains("CredentialVersion", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Claims,
            candidate => candidate.Type.Contains("SessionHandle", StringComparison.Ordinal) ||
                candidate.Type.Contains("SecretHash", StringComparison.Ordinal));

        AuthenticationSession persisted = await GetSessionAsync(seeded.SessionId);
        Assert.Equal(Now, persisted.LastSeenAt);
        Assert.Equal(Now.Add(AuthenticationSessionPolicy.IdleLifetime), persisted.IdleExpiresAt);
        Assert.Equal(2, persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task GetProtectedProbe_MissingCookie_ReturnsEmptyNoStoreUnauthorizedWithoutCreatingSession()
    {
        using HttpResponseMessage response = await client.GetAsync(ProbePath);

        await AssertUnauthorizedAsync(response);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuthenticationSessions.CountAsync());
    }

    [Fact]
    public async Task GetProtectedProbe_MalformedCookie_ReturnsEmptyNoStoreUnauthorizedWithoutChangingSession()
    {
        SeededSession seeded = await SeedSessionAsync();

        using HttpResponseMessage response = await GetProbeAsync("malformed");

        await AssertUnauthorizedAsync(response);
        await AssertSessionUnchangedAsync(seeded);
    }

    [Fact]
    public async Task GetProtectedProbe_RevokedSession_ReturnsEmptyNoStoreUnauthorizedWithoutChangingSession()
    {
        SeededSession seeded = await SeedSessionAsync(revoked: true);

        using HttpResponseMessage response = await GetProbeAsync(seeded.RawHandle);

        await AssertUnauthorizedAsync(response);
        await AssertSessionUnchangedAsync(seeded);
    }

    private async Task<SeededSession> SeedSessionAsync(bool revoked = false)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var user = new User(
            "HTTP Session User",
            $"http-session-{Guid.NewGuid():N}@example.test",
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
            Now.AddMinutes(5),
            Now.AddHours(2));

        if (revoked)
        {
            session.Revoke(Now.AddMinutes(-1));
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return new SeededSession(
            user.Id,
            session.Id,
            rawHandle,
            session.LastSeenAt,
            session.IdleExpiresAt,
            session.ConcurrencyVersion,
            session.RevokedAt);
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

    private async Task AssertSessionUnchangedAsync(SeededSession expected)
    {
        AuthenticationSession persisted = await GetSessionAsync(expected.SessionId);
        Assert.Equal(expected.LastSeenAt, persisted.LastSeenAt);
        Assert.Equal(expected.IdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(expected.ConcurrencyVersion, persisted.ConcurrencyVersion);
        Assert.Equal(expected.RevokedAt, persisted.RevokedAt);
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.False(response.Headers.Contains(HeaderNames.WWWAuthenticate));
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
                        .MapGet(
                            ProbePath,
                            (HttpContext context) =>
                            {
                                ClaimValue[] claims = context.User.Claims
                                    .Select(claim => new ClaimValue(
                                        claim.Type,
                                        claim.Value))
                                    .ToArray();

                                return Results.Json(new AuthenticationProbeResponse(
                                    context.User.Identity?.IsAuthenticated == true,
                                    context.User.Identity?.AuthenticationType,
                                    claims));
                            })
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

    private sealed record AuthenticationProbeResponse(
        bool IsAuthenticated,
        string? AuthenticationType,
        ClaimValue[] Claims);

    private sealed record ClaimValue(string Type, string Value);

    private sealed record SeededSession(
        Guid UserId,
        Guid SessionId,
        string RawHandle,
        DateTimeOffset LastSeenAt,
        DateTimeOffset IdleExpiresAt,
        long ConcurrencyVersion,
        DateTimeOffset? RevokedAt);
}
