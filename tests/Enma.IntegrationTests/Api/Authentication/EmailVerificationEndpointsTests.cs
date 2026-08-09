using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enma.IntegrationTests.Api.Authentication;

public sealed class EmailVerificationEndpointsTests : IDisposable
{
    private const string ResendPath =
        "/api/auth/email-verification/resend";
    private const string VerifyPath =
        "/api/auth/email-verification/verify";
    private const string RawToken =
        "synthetic-email-verification-token-that-must-not-leak";

    private static readonly Guid UserId = Guid.Parse(
        "11111111-2222-3333-4444-555555555555");

    private readonly WebApplicationFactory<Program> factory;
    private readonly WebApplicationFactory<Program> testFactory;
    private readonly StubUserLookup userLookup = new();
    private readonly StubTokenService tokenService = new();
    private readonly StubChallengePersistence persistence = new();
    private readonly StubDelivery delivery = new();
    private readonly HttpClient client;

    public EmailVerificationEndpointsTests()
    {
        factory = new WebApplicationFactory<Program>();
        testFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:Database",
                "Host=localhost;Database=enma-tests");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailVerificationUserLookup>();
                services.RemoveAll<IEmailVerificationTokenService>();
                services.RemoveAll<IEmailVerificationChallengePersistence>();
                services.RemoveAll<IEmailVerificationDelivery>();
                services.AddSingleton<IEmailVerificationUserLookup>(userLookup);
                services.AddSingleton<IEmailVerificationTokenService>(tokenService);
                services.AddSingleton<IEmailVerificationChallengePersistence>(
                    persistence);
                services.AddSingleton<IEmailVerificationDelivery>(delivery);
            });
        });
        client = testFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public void Dispose()
    {
        client.Dispose();
        testFactory.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task PostResend_MalformedEmail_ReturnsAccepted()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ResendPath,
            new { Email = "not-an-email" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, userLookup.CallCount);
    }

    [Fact]
    public async Task PostResend_UnknownEmail_ReturnsAccepted()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ResendPath,
            new { Email = "unknown@example.test" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Equal(1, userLookup.CallCount);
        Assert.Equal("unknown@example.test", userLookup.NormalizedEmail);
    }

    [Fact]
    public async Task PostResend_DeliveryFailure_ReturnsGenericNoStoreResponse()
    {
        userLookup.UserId = UserId;
        persistence.IssuanceResult =
            EmailVerificationChallengeIssuancePersistenceResult.CreateSucceeded(
                "user@example.test");
        delivery.Result = EmailVerificationDeliveryResult.Failed;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ResendPath,
            new { Email = "user@example.test" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Equal(1, delivery.CallCount);
    }

    [Fact]
    public async Task PostResend_SixRequestsFromSameClient_RateLimitsSixthRequest()
    {
        for (int requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            using HttpResponseMessage admittedResponse = await client.PostAsJsonAsync(
                ResendPath,
                new { Email = "not-an-email" });

            Assert.Equal(HttpStatusCode.Accepted, admittedResponse.StatusCode);
        }

        using HttpResponseMessage rejectedResponse = await client.PostAsJsonAsync(
            ResendPath,
            new { Email = "not-an-email" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(
            string.Empty,
            await rejectedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostVerify_MalformedToken_ReturnsGenericInvalidProblem()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = "malformed" });

        await AssertInvalidProblemAsync(response);
        Assert.Equal(0, persistence.ConsumeCallCount);
    }

    [Fact]
    public async Task PostVerify_RejectedPersistence_ReturnsSameGenericInvalidProblem()
    {
        tokenService.HashSucceeds = true;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = RawToken });

        string rawResponse = await AssertInvalidProblemAsync(response);
        Assert.DoesNotContain(RawToken, rawResponse, StringComparison.Ordinal);
        Assert.Equal(1, persistence.ConsumeCallCount);
    }

    [Fact]
    public async Task PostVerify_SucceededVerification_ReturnsEmptyNoStoreResponse()
    {
        tokenService.HashSucceeds = true;
        persistence.ConsumptionResult =
            EmailVerificationChallengeConsumptionPersistenceResult.Succeeded;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = RawToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task PostVerify_TwentyOneRequestsFromSameClient_RateLimitsLastRequest()
    {
        for (int requestNumber = 1; requestNumber <= 20; requestNumber++)
        {
            using HttpResponseMessage admittedResponse = await client.PostAsJsonAsync(
                VerifyPath,
                new { Token = RawToken });

            await AssertInvalidProblemAsync(admittedResponse);
        }

        using HttpResponseMessage rejectedResponse = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = RawToken });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.CacheControl?.NoStore);
        string rawResponse = await rejectedResponse.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, rawResponse);
        Assert.DoesNotContain(RawToken, rawResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostEmailVerification_ExhaustedResendBudget_VerifyRemainsAdmitted()
    {
        for (int requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            using HttpResponseMessage resendResponse = await client.PostAsJsonAsync(
                ResendPath,
                new { Email = "not-an-email" });

            Assert.Equal(HttpStatusCode.Accepted, resendResponse.StatusCode);
        }

        using HttpResponseMessage verifyResponse = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = "malformed" });

        await AssertInvalidProblemAsync(verifyResponse);
    }

    private static async Task<string> AssertInvalidProblemAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);

        string rawResponse = await response.Content.ReadAsStringAsync();
        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            rawResponse,
            JsonSerializerOptions.Web);
        Assert.NotNull(problemDetails);
        Assert.Equal((int)HttpStatusCode.BadRequest, problemDetails.Status);
        Assert.Equal(VerifyPath, problemDetails.Instance);
        Assert.True(problemDetails.Extensions.TryGetValue("code", out object? code));
        JsonElement codeElement = Assert.IsType<JsonElement>(code);
        Assert.Equal("email_verification_invalid", codeElement.GetString());

        return rawResponse;
    }

    private sealed class StubUserLookup : IEmailVerificationUserLookup
    {
        public Guid? UserId { get; set; }

        public int CallCount { get; private set; }

        public string? NormalizedEmail { get; private set; }

        public Task<Guid?> FindUserIdByEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            NormalizedEmail = normalizedEmail;
            return Task.FromResult(UserId);
        }
    }

    private sealed class StubTokenService : IEmailVerificationTokenService
    {
        public EmailVerificationTokenHash TokenHash { get; } =
            new(CreateHashBytes());

        public bool HashSucceeds { get; set; }

        public string GenerateToken(out EmailVerificationTokenHash tokenHash)
        {
            tokenHash = TokenHash;
            return RawToken;
        }

        public bool TryHashToken(
            string? rawToken,
            out EmailVerificationTokenHash? tokenHash)
        {
            tokenHash = HashSucceeds ? TokenHash : null;
            return HashSucceeds;
        }
    }

    private sealed class StubChallengePersistence
        : IEmailVerificationChallengePersistence
    {
        public EmailVerificationChallengeIssuancePersistenceResult IssuanceResult
        { get; set; } = EmailVerificationChallengeIssuancePersistenceResult.Rejected;

        public EmailVerificationChallengeConsumptionPersistenceResult ConsumptionResult
        { get; set; } = EmailVerificationChallengeConsumptionPersistenceResult.Rejected;

        public int ConsumeCallCount { get; private set; }

        public Task<EmailVerificationChallengeIssuancePersistenceResult>
            TryIssueOrRotateAsync(
                Guid userId,
                EmailVerificationTokenHash tokenHash,
                TimeSpan tokenLifetime,
                TimeSpan resendCooldown,
                CancellationToken cancellationToken = default)
        {
            return Task.FromResult(IssuanceResult);
        }

        public Task<EmailVerificationChallengeConsumptionPersistenceResult>
            TryConsumeAsync(
                EmailVerificationTokenHash tokenHash,
                CancellationToken cancellationToken = default)
        {
            ConsumeCallCount++;
            return Task.FromResult(ConsumptionResult);
        }
    }

    private sealed class StubDelivery : IEmailVerificationDelivery
    {
        public EmailVerificationDeliveryResult Result { get; set; }

        public int CallCount { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private static byte[] CreateHashBytes()
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(index + 1))
            .ToArray();
    }
}
