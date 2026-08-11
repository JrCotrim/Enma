using System.Net;
using System.Net.Http.Json;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class LoginEndpointTests : IAsyncLifetime
{
    private const string LoginPath = "/api/auth/login";
    private const string VerifyPath = "/api/auth/email-verification/verify";
    private const string SessionCookieName = "__Host-enma_session";
    private const string Email = "http-login-owner@example.test";
    private const string Password = "Correct-Synthetic-Password-123!";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LoginEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostLogin_VerifiedUser_ReturnsSecureSessionCookieWithoutRawHandlePersistence()
    {
        await SeedUserAsync(emailVerified: true);

        using HttpResponseMessage response = await PostLoginAsync(Email, Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, responseBody);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);

        SetCookieHeaderValue sessionCookie = ParseOnlySetCookie(response);
        Assert.Equal(SessionCookieName, sessionCookie.Name.ToString());
        Assert.True(sessionCookie.Secure);
        Assert.True(sessionCookie.HttpOnly);
        Assert.Equal("/", sessionCookie.Path.ToString());
        Assert.Equal("Lax", sessionCookie.SameSite.ToString());
        Assert.False(sessionCookie.Domain.HasValue);
        Assert.Null(sessionCookie.Expires);
        Assert.Null(sessionCookie.MaxAge);

        string rawHandle = sessionCookie.Value.ToString();
        Assert.False(string.IsNullOrWhiteSpace(rawHandle));
        Assert.False(responseBody.Contains(rawHandle, StringComparison.Ordinal));

        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        Assert.True(handleService.TryHashHandle(rawHandle, out var expectedHash));
        Assert.NotNull(expectedHash);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await dbContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(expectedHash, persistedSession.SecretHash);
        Assert.False(string.Equals(
            rawHandle,
            Convert.ToBase64String(persistedSession.SecretHash.ToArray()),
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostLogin_UnknownValidEmail_ReturnsGenericUnauthorizedResponse()
    {
        using HttpResponseMessage response = await PostLoginAsync(
            "unknown-http-login-owner@example.test",
            Password);

        await AssertGenericUnauthorizedAsync(response);
    }

    [Fact]
    public async Task PostLogin_WrongPassword_ReturnsGenericUnauthorizedWithoutClearingExistingCookie()
    {
        await SeedUserAsync(emailVerified: true);
        client.DefaultRequestHeaders.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}=existing-browser-session");

        using HttpResponseMessage response = await PostLoginAsync(
            Email,
            "wrong-synthetic-password");

        await AssertGenericUnauthorizedAsync(response);
    }

    [Fact]
    public async Task PostLogin_UnverifiedUser_ReturnsGenericUnauthorizedResponse()
    {
        await SeedUserAsync(emailVerified: false);

        using HttpResponseMessage response = await PostLoginAsync(Email, Password);

        await AssertGenericUnauthorizedAsync(response);
    }

    [Fact]
    public async Task PostLogin_EleventhRequestFromSameClient_IsRateLimitedIndependently()
    {
        for (int requestNumber = 1; requestNumber <= 10; requestNumber++)
        {
            using HttpResponseMessage admittedResponse = await PostLoginAsync(
                null,
                null);

            await AssertGenericUnauthorizedAsync(admittedResponse);
        }

        using HttpResponseMessage rejectedResponse = await PostLoginAsync(null, null);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(
            string.Empty,
            await rejectedResponse.Content.ReadAsStringAsync());
        Assert.False(rejectedResponse.Headers.Contains(HeaderNames.SetCookie));

        using HttpResponseMessage verifyResponse = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = "malformed" });

        Assert.Equal(HttpStatusCode.BadRequest, verifyResponse.StatusCode);
    }

    private Task<HttpResponseMessage> PostLoginAsync(
        string? email,
        string? password)
    {
        return client.PostAsJsonAsync(LoginPath, new { Email = email, Password = password });
    }

    private async Task SeedUserAsync(bool emailVerified)
    {
        var user = new User("HTTP Login Owner", Email, CreatedAt);

        if (emailVerified)
        {
            user.VerifyEmail(CreatedAt.AddMinutes(5));
        }

        var passwordHasher = new AspNetCorePasswordHasher(
            new PasswordHasher<object>());
        var credential = new UserCredential(
            user.Id,
            passwordHasher.HashPassword(Password),
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        await dbContext.SaveChangesAsync();
    }

    private static SetCookieHeaderValue ParseOnlySetCookie(
        HttpResponseMessage response)
    {
        bool hasSetCookie = response.Headers.TryGetValues(
            HeaderNames.SetCookie,
            out IEnumerable<string>? values);
        Assert.True(hasSetCookie);
        Assert.NotNull(values);

        string[] headers = values.ToArray();
        Assert.True(headers.Length == 1);
        bool parsed = SetCookieHeaderValue.TryParse(
            headers[0],
            out SetCookieHeaderValue? setCookie);
        Assert.True(parsed);
        Assert.NotNull(setCookie);

        return setCookie;
    }

    private static async Task AssertGenericUnauthorizedAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.False(response.Headers.Contains(HeaderNames.WWWAuthenticate));
    }
}
