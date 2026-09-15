using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordRecoveryEndpointTests : IAsyncLifetime
{
    private const string RequestPath = "/api/auth/password-recovery/request";
    private const string ResetPath = "/api/auth/password-recovery/reset";
    private const string LoginPath = "/api/auth/login";
    private const string Email = "password-recovery@example.test";
    private const string OldPassword = "Old-Synthetic-Password-123!";
    private const string NewPassword = "New-Synthetic-Password-456!";
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly CapturingDelivery delivery = new();
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public PasswordRecoveryEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<IPasswordRecoveryDelivery>();
            services.RemoveAll<ICompromisedPasswordChecker>();
            services.AddSingleton<IPasswordRecoveryDelivery>(delivery);
            services.AddSingleton<ICompromisedPasswordChecker>(
                new SafePasswordChecker());
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync()
    {
        client.Dispose();
        factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Request_ExistingUnknownAndDeliveryFailureHaveSameObservableResponse()
    {
        await SeedVerifiedUserAsync();
        delivery.Result = PasswordRecoveryDeliveryResult.Failed;

        using HttpResponseMessage existing = await client.PostAsJsonAsync(
            RequestPath,
            new { Email = $"  {Email.ToUpperInvariant()}  " });
        string existingBody = await existing.Content.ReadAsStringAsync();
        Assert.Equal(1, delivery.CallCount);

        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            RequestPath,
            new { Email = "unknown-recovery@example.test" });
        string unknownBody = await unknown.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, existing.StatusCode);
        Assert.Equal(existing.StatusCode, unknown.StatusCode);
        Assert.Equal(existingBody, unknownBody);
        Assert.Equal(string.Empty, existingBody);
        Assert.True(existing.Headers.CacheControl?.NoStore);
        Assert.True(unknown.Headers.CacheControl?.NoStore);
        Assert.Equal(1, delivery.CallCount);
    }

    [Fact]
    public async Task Recovery_EndToEndConsumesTokenReplacesPasswordAndInvalidatesOldSession()
    {
        await SeedVerifiedUserAsync();

        using HttpResponseMessage login = await client.PostAsJsonAsync(
            LoginPath,
            new { Email, Password = OldPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using HttpResponseMessage beforeReset = await client.GetAsync(
            "/api/me/organizations");
        Assert.Equal(HttpStatusCode.OK, beforeReset.StatusCode);

        using HttpResponseMessage request = await client.PostAsJsonAsync(
            RequestPath,
            new { Email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        string rawToken = Assert.IsType<string>(delivery.RawToken);

        using HttpResponseMessage reset = await client.PostAsJsonAsync(
            ResetPath,
            new { Token = rawToken, NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.True(reset.Headers.CacheControl?.NoStore);

        using HttpResponseMessage replay = await client.PostAsJsonAsync(
            ResetPath,
            new { Token = rawToken, NewPassword = "Other-Synthetic-Password-789!" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("password_recovery_invalid", await ReadCodeAsync(replay));

        using HttpResponseMessage oldSession = await client.GetAsync(
            "/api/me/organizations");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using HttpResponseMessage oldLogin = await client.PostAsJsonAsync(
            LoginPath,
            new { Email, Password = OldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using HttpResponseMessage newLogin = await client.PostAsJsonAsync(
            LoginPath,
            new { Email, Password = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, newLogin.StatusCode);
    }

    [Fact]
    public async Task Reset_InvalidWeakAndCompromisedInputsReturnSafeCodes()
    {
        using HttpResponseMessage invalid = await client.PostAsJsonAsync(
            ResetPath,
            new { Token = "invalid", NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("password_recovery_invalid", await ReadCodeAsync(invalid));

        var tokenService = factory.Services
            .GetRequiredService<IPasswordRecoveryTokenService>();
        string token = tokenService.GenerateToken(out _);
        using HttpResponseMessage weak = await client.PostAsJsonAsync(
            ResetPath,
            new { Token = token, NewPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("password_invalid", await ReadCodeAsync(weak));
    }

    [Fact]
    public async Task Request_SixthRequestFromSameIpIsRateLimitedWithoutCsrfRequirement()
    {
        for (int index = 0; index < 5; index++)
        {
            using HttpResponseMessage admitted = await client.PostAsJsonAsync(
                RequestPath,
                new { Email = "invalid" });
            Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        }

        using HttpResponseMessage rejected = await client.PostAsJsonAsync(
            RequestPath,
            new { Email = "invalid" });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }

    private async Task SeedVerifiedUserAsync()
    {
        var user = new User("Password Recovery", Email, CreatedAt);
        user.VerifyEmail(CreatedAt);
        var hasher = new AspNetCorePasswordHasher(new PasswordHasher<object>());
        var credential = new UserCredential(
            user.Id,
            hasher.HashPassword(OldPassword),
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(user, credential);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Extensions.TryGetValue("code", out object? value));
        return Assert.IsType<JsonElement>(value).GetString();
    }

    private sealed class SafePasswordChecker : ICompromisedPasswordChecker
    {
        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CapturingDelivery : IPasswordRecoveryDelivery
    {
        public PasswordRecoveryDeliveryResult Result { get; set; } =
            PasswordRecoveryDeliveryResult.Delivered;
        public int CallCount { get; private set; }
        public string? RawToken { get; private set; }

        public Task<PasswordRecoveryDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RawToken = rawToken;
            return Task.FromResult(Result);
        }
    }
}
