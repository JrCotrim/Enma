using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Email;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class OwnerOnboardingEmailVerificationEndToEndTests
    : IAsyncLifetime
{
    private const ushort SmtpContainerPort = 1025;
    private const ushort ApiContainerPort = 8025;
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
    private const string OnboardingPath = "/api/onboarding/register";
    private const string VerifyPath = "/api/auth/email-verification/verify";
    private const string VerificationPageUrl =
        "https://app.example/verify-email";

    private static readonly Regex VerificationUrlPattern = new(
        "https://[^\\s<>\"']+",
        RegexOptions.CultureInvariant);
    private static readonly Regex VerificationTokenPattern = new(
        "^[A-Za-z0-9_-]{43}$",
        RegexOptions.CultureInvariant);

    private readonly PostgreSqlFixture fixture;
    private readonly SafeCompromisedPasswordChecker compromisedPasswordChecker =
        new();
    private readonly IContainer mailpit = new ContainerBuilder(MailpitImage)
        .WithPortBinding(SmtpContainerPort, true)
        .WithPortBinding(ApiContainerPort, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(ApiContainerPort)
                    .ForPath("/api/v1/messages")))
        .Build();

    private EnmaApiFactory? factory;
    private WebApplicationFactory<Program>? testFactory;
    private HttpClient? client;

    public OwnerOnboardingEmailVerificationEndToEndTests(
        PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();

        using var startupTimeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(2));
        await mailpit.StartAsync(startupTimeout.Token);

        factory = new EnmaApiFactory(fixture);
        testFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                string deliverySection =
                    EmailVerificationDeliveryOptions.SectionName;
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{deliverySection}:VerificationPageUrl"] =
                            VerificationPageUrl,
                        [$"{deliverySection}:SenderName"] = "ENMA",
                        [$"{deliverySection}:SenderAddress"] =
                            "no-reply@example.test",
                        [$"{deliverySection}:SmtpHost"] = "127.0.0.1",
                        [$"{deliverySection}:SmtpPort"] = mailpit
                            .GetMappedPublicPort(SmtpContainerPort)
                            .ToString(),
                        [$"{deliverySection}:SmtpSecurity"] = "None",
                        [$"{deliverySection}:SmtpUsername"] = string.Empty,
                        [$"{deliverySection}:SmtpPassword"] = string.Empty
                    });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICompromisedPasswordChecker>();
                services.AddSingleton<ICompromisedPasswordChecker>(
                    compromisedPasswordChecker);

                // The isolated Mailpit fixture has no publicly trusted TLS
                // certificate. Production option validation is covered
                // separately; the production delivery chain remains intact.
                services.RemoveAll<
                    IValidateOptions<EmailVerificationDeliveryOptions>>();
            });
        });
        client = testFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task PostOnboarding_ActualMailpitToken_VerifiesOnceEndToEnd()
    {
        WebApplicationFactory<Program> application = testFactory
            ?? throw new InvalidOperationException(
                "The test application has not been initialized.");
        HttpClient httpClient = client
            ?? throw new InvalidOperationException(
                "The test HTTP client has not been initialized.");
        string uniqueValue = Guid.NewGuid().ToString("N");
        string organizationSlug = $"phase8b-{uniqueValue}";
        string ownerEmail = $"phase8b-{uniqueValue}@example.test";
        var request = new RegisterOrganizationOwnerRequest
        {
            OrganizationName = $"Phase 8B {uniqueValue}",
            OrganizationSlug = organizationSlug,
            OwnerName = "Phase 8B Owner",
            OwnerEmail = $"  {ownerEmail.ToUpperInvariant()}  ",
            Password = "EndToEnd!Owner42"
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using (IServiceScope scope = application.Services.CreateScope())
        {
            Assert.IsType<BudgetedEmailVerificationDelivery>(
                scope.ServiceProvider
                    .GetRequiredService<IEmailVerificationDelivery>());
            Assert.IsType<PostgreSqlEmailVerificationSendBudget>(
                scope.ServiceProvider
                    .GetRequiredService<IEmailVerificationSendBudget>());
            Assert.IsType<MailKitEmailVerificationDelivery>(
                scope.ServiceProvider
                    .GetRequiredService<MailKitEmailVerificationDelivery>());
        }

        using HttpResponseMessage onboardingResponse = await httpClient
            .PostAsJsonAsync(OnboardingPath, request, timeout.Token);

        Assert.Equal(HttpStatusCode.Created, onboardingResponse.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding = await onboardingResponse
            .Content
            .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>(
                timeout.Token);
        Assert.NotNull(onboarding);
        Assert.Equal(ownerEmail, onboarding.UserEmail);
        Assert.Equal(1, compromisedPasswordChecker.CallCount);

        await AssertPreVerificationStateAsync(
            onboarding,
            organizationSlug,
            ownerEmail,
            timeout.Token);

        MimeMessage message = await GetMessageForRecipientAsync(
            ownerEmail,
            timeout.Token);
        MailboxAddress recipient = Assert.IsType<MailboxAddress>(
            Assert.Single(message.To));
        Assert.Equal(ownerEmail, recipient.Address);
        Assert.Equal(MailKitEmailVerificationDelivery.Subject, message.Subject);
        Assert.IsType<MultipartAlternative>(message.Body);
        Assert.NotNull(message.TextBody);
        Assert.NotNull(message.HtmlBody);

        MatchCollection verificationUrlMatches = VerificationUrlPattern.Matches(
            message.TextBody);
        if (verificationUrlMatches.Count != 1)
        {
            Assert.Fail(
                "The text message must contain exactly one HTTPS verification URL.");
        }

        Assert.True(Uri.TryCreate(
            verificationUrlMatches[0].Value,
            UriKind.Absolute,
            out Uri? parsedVerificationUri));
        Uri verificationUri = Assert.IsType<Uri>(parsedVerificationUri);

        Assert.Equal(Uri.UriSchemeHttps, verificationUri.Scheme);
        Assert.Equal("/verify-email", verificationUri.AbsolutePath);
        Assert.Equal(string.Empty, verificationUri.Query);
        if (!verificationUri.Fragment.StartsWith(
                "#token=",
                StringComparison.Ordinal))
        {
            Assert.Fail(
                "The verification URL must carry the token in its fragment.");
        }

        Assert.Equal(
            "#token=".Length + 43,
            verificationUri.Fragment.Length);
        Assert.Equal(
            1,
            CountOccurrences(verificationUri.Fragment, "token="));
        Assert.False(verificationUri.Fragment.Contains('&'));
        Assert.False(verificationUri.Fragment.Contains('?'));

        string rawToken = verificationUri.Fragment["#token=".Length..];
        if (!VerificationTokenPattern.IsMatch(rawToken))
        {
            Assert.Fail(
                "The delivered verification token has an invalid grammar.");
        }

        Assert.False(verificationUri.AbsolutePath.Contains(
            rawToken,
            StringComparison.Ordinal));
        Assert.False(verificationUri.Query.Contains(
            rawToken,
            StringComparison.Ordinal));
        Assert.Equal(
            1,
            CountOccurrences(verificationUri.AbsoluteUri, rawToken));
        Assert.Equal(1, CountOccurrences(message.TextBody, rawToken));
        Assert.Equal(1, CountOccurrences(message.HtmlBody, rawToken));
        Assert.True(message.TextBody.Contains(
            verificationUri.AbsoluteUri,
            StringComparison.Ordinal));
        Assert.True(message.HtmlBody.Contains(
            verificationUri.AbsoluteUri,
            StringComparison.Ordinal));
        Assert.False(message.TextBody.Contains("?token=", StringComparison.Ordinal));
        Assert.False(message.HtmlBody.Contains("?token=", StringComparison.Ordinal));
        if (message.Headers.Any(header => header.Value.Contains(
                rawToken,
                StringComparison.Ordinal)))
        {
            Assert.Fail(
                "A MIME header must not contain the verification token.");
        }

        using HttpResponseMessage verifyResponse = await httpClient
            .PostAsJsonAsync(
                VerifyPath,
                new { Token = rawToken },
                timeout.Token);

        Assert.Equal(HttpStatusCode.NoContent, verifyResponse.StatusCode);
        Assert.True(verifyResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(
            string.Empty,
            await verifyResponse.Content.ReadAsStringAsync(timeout.Token));

        await AssertPostVerificationStateAsync(onboarding, timeout.Token);

        using HttpResponseMessage reusedTokenResponse = await httpClient
            .PostAsJsonAsync(
                VerifyPath,
                new { Token = rawToken },
                timeout.Token);

        await AssertGenericInvalidResponseAsync(
            reusedTokenResponse,
            rawToken,
            onboarding,
            ownerEmail,
            timeout.Token);
        await AssertPostVerificationStateAsync(onboarding, timeout.Token);
    }

    private async Task AssertPreVerificationStateAsync(
        RegisterOrganizationOwnerResponse onboarding,
        string organizationSlug,
        string ownerEmail,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == onboarding.UserId,
                cancellationToken);
        EmailVerificationChallenge challenge = await dbContext
            .EmailVerificationChallenges
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.UserId == onboarding.UserId,
                cancellationToken);

        Assert.Equal(1, await dbContext.Organizations.CountAsync(
            candidate => candidate.Id == onboarding.OrganizationId
                && candidate.Slug == organizationSlug,
            cancellationToken));
        Assert.Equal(ownerEmail, user.Email);
        Assert.Null(user.EmailVerifiedAt);
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync(
            candidate => candidate.Id == onboarding.MembershipId
                && candidate.OrganizationId == onboarding.OrganizationId
                && candidate.UserId == onboarding.UserId,
            cancellationToken));
        Assert.Equal(1, await dbContext.UserCredentials.CountAsync(
            candidate => candidate.UserId == onboarding.UserId,
            cancellationToken));
        Assert.Equal(1, await dbContext.EmailVerificationChallenges.CountAsync(
            cancellationToken));
        Assert.Equal(onboarding.UserId, challenge.UserId);
        Assert.Equal(ownerEmail, challenge.EmailAtIssue);
        Assert.NotNull(challenge.TokenHash);
        Assert.Equal(2, await dbContext.EmailVerificationSendBudgets.CountAsync(
            cancellationToken));

        string[] tokenPropertyNames = typeof(EmailVerificationChallenge)
            .GetProperties()
            .Where(property => property.Name.Contains(
                "Token",
                StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["TokenHash"], tokenPropertyNames);
    }

    private async Task AssertPostVerificationStateAsync(
        RegisterOrganizationOwnerResponse onboarding,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == onboarding.UserId,
                cancellationToken);

        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Equal(0, await dbContext.EmailVerificationChallenges.CountAsync(
            candidate => candidate.UserId == onboarding.UserId,
            cancellationToken));
        Assert.Equal(1, await dbContext.Organizations.CountAsync(
            candidate => candidate.Id == onboarding.OrganizationId,
            cancellationToken));
        Assert.Equal(1, await dbContext.Users.CountAsync(
            candidate => candidate.Id == onboarding.UserId,
            cancellationToken));
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync(
            candidate => candidate.Id == onboarding.MembershipId
                && candidate.OrganizationId == onboarding.OrganizationId
                && candidate.UserId == onboarding.UserId,
            cancellationToken));
        Assert.Equal(1, await dbContext.UserCredentials.CountAsync(
            candidate => candidate.UserId == onboarding.UserId,
            cancellationToken));
    }

    private static async Task AssertGenericInvalidResponseAsync(
        HttpResponseMessage response,
        string rawToken,
        RegisterOrganizationOwnerResponse onboarding,
        string ownerEmail,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);

        string rawResponse = await response.Content.ReadAsStringAsync(
            cancellationToken);
        Assert.False(rawResponse.Contains(rawToken, StringComparison.Ordinal));
        Assert.False(rawResponse.Contains(ownerEmail, StringComparison.Ordinal));
        Assert.False(rawResponse.Contains(
            onboarding.UserId.ToString(),
            StringComparison.OrdinalIgnoreCase));
        Assert.False(rawResponse.Contains("already used", StringComparison.OrdinalIgnoreCase));
        Assert.False(rawResponse.Contains("challenge", StringComparison.OrdinalIgnoreCase));
        Assert.False(rawResponse.Contains("tokenHash", StringComparison.OrdinalIgnoreCase));
        Assert.False(rawResponse.Contains("verifiedAt", StringComparison.OrdinalIgnoreCase));

        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            rawResponse,
            JsonSerializerOptions.Web);
        Assert.NotNull(problemDetails);
        Assert.Equal("Invalid email verification", problemDetails.Title);
        Assert.Equal(
            "The email verification request is invalid.",
            problemDetails.Detail);
        Assert.Equal((int)HttpStatusCode.BadRequest, problemDetails.Status);
        Assert.Equal(VerifyPath, problemDetails.Instance);
        Assert.True(problemDetails.Extensions.TryGetValue(
            "code",
            out object? code));
        JsonElement codeElement = Assert.IsType<JsonElement>(code);
        Assert.Equal("email_verification_invalid", codeElement.GetString());

        using JsonDocument document = JsonDocument.Parse(rawResponse);
        string[] allowedProperties =
        [
            "type",
            "title",
            "status",
            "detail",
            "instance",
            "code",
            "traceId"
        ];
        Assert.True(document.RootElement
            .EnumerateObject()
            .All(property => allowedProperties.Contains(
                property.Name,
                StringComparer.Ordinal)));
    }

    private async Task<MimeMessage> GetMessageForRecipientAsync(
        string recipient,
        CancellationToken cancellationToken)
    {
        using var mailpitClient = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(ApiContainerPort)}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        string query = Uri.EscapeDataString($"to:\"{recipient}\"");

        while (true)
        {
            using HttpResponseMessage searchResponse = await mailpitClient.GetAsync(
                $"api/v1/search?query={query}&limit=2",
                cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            using JsonDocument searchResult = await JsonDocument.ParseAsync(
                await searchResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            JsonElement messages = searchResult.RootElement.GetProperty("messages");

            if (messages.GetArrayLength() > 1)
            {
                throw new InvalidOperationException(
                    "Mailpit returned more than one message for the unique recipient.");
            }

            if (messages.GetArrayLength() == 1)
            {
                string? messageId = messages[0].GetProperty("ID").GetString();

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new InvalidOperationException(
                        "Mailpit returned a message without an identifier.");
                }

                await using Stream rawMessage = await mailpitClient.GetStreamAsync(
                    $"api/v1/message/{Uri.EscapeDataString(messageId)}/raw",
                    cancellationToken);
                return await MimeMessage.LoadAsync(rawMessage, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static int CountOccurrences(string value, string searchValue)
    {
        int count = 0;
        int startIndex = 0;

        while ((startIndex = value.IndexOf(
            searchValue,
            startIndex,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += searchValue.Length;
        }

        return count;
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();

        if (testFactory is not null)
        {
            await testFactory.DisposeAsync();
        }

        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await mailpit.DisposeAsync();
    }

    private sealed class SafeCompromisedPasswordChecker
        : ICompromisedPasswordChecker
    {
        public int CallCount { get; private set; }

        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(false);
        }
    }
}
