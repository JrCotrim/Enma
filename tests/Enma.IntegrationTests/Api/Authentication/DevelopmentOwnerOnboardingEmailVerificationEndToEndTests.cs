using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Authentication;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Email;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MimeKit;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class DevelopmentOwnerOnboardingEmailVerificationEndToEndTests
    : IAsyncLifetime
{
    private const ushort SmtpContainerPort = 1025;
    private const ushort ApiContainerPort = 8025;
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
    private const string OnboardingPath = "/api/onboarding/register";
    private const string LoginPath = "/api/auth/login";
    private const string VerifyPath = "/api/auth/email-verification/verify";
    private const string Password = "Development!Owner42";

    private static readonly Regex VerificationUrlPattern = new(
        "http://[^\\s<>\"']+",
        RegexOptions.CultureInvariant);

    private readonly PostgreSqlFixture fixture;
    private readonly CapturingLoggerProvider loggerProvider = new();
    private readonly IContainer mailpit = new ContainerBuilder(MailpitImage)
        .WithPortBinding(SmtpContainerPort, true)
        .WithPortBinding(ApiContainerPort, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(ApiContainerPort)
                    .ForPath("/api/v1/messages")))
        .Build();
    private DevelopmentEnmaApiFactory? factory;
    private HttpClient? client;

    public DevelopmentOwnerOnboardingEmailVerificationEndToEndTests(
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

        factory = new DevelopmentEnmaApiFactory(
            fixture,
            loggerProvider,
            mailpit.GetMappedPublicPort(SmtpContainerPort));
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task DevelopmentOnboarding_MailpitFragment_VerifiesBeforeLoginSucceeds()
    {
        DevelopmentEnmaApiFactory application = factory
            ?? throw new InvalidOperationException(
                "The test application has not been initialized.");
        HttpClient httpClient = client
            ?? throw new InvalidOperationException(
                "The test HTTP client has not been initialized.");
        string uniqueValue = Guid.NewGuid().ToString("N");
        string ownerEmail = $"development-{uniqueValue}@example.test";
        var request = new RegisterOrganizationOwnerRequest
        {
            OrganizationName = $"Development {uniqueValue}",
            OrganizationSlug = $"development-{uniqueValue}",
            OwnerName = "Development Owner",
            OwnerEmail = ownerEmail,
            Password = Password
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using (IServiceScope scope = application.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider
                .GetRequiredService<RegisterOrganizationOwnerHandler>());
            Assert.IsType<BudgetedEmailVerificationDelivery>(scope.ServiceProvider
                .GetRequiredService<IEmailVerificationDelivery>());
            Assert.IsType<DevelopmentEmailVerificationDelivery>(
                application.Services
                    .GetRequiredService<DevelopmentEmailVerificationDelivery>());
            Assert.Null(application.Services
                .GetService<MailKitEmailVerificationDelivery>());
        }

        using HttpResponseMessage onboardingResponse = await httpClient
            .PostAsJsonAsync(OnboardingPath, request, timeout.Token);

        Assert.Equal(HttpStatusCode.Created, onboardingResponse.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding = await onboardingResponse
            .Content
            .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>(timeout.Token);
        Assert.NotNull(onboarding);
        Assert.Equal(ownerEmail, onboarding.UserEmail);

        using HttpResponseMessage preVerificationLogin = await httpClient
            .PostAsJsonAsync(
                LoginPath,
                new { Email = ownerEmail, Password },
                timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, preVerificationLogin.StatusCode);
        Assert.True(preVerificationLogin.Headers.CacheControl?.NoStore);
        Assert.False(preVerificationLogin.Headers.Contains(HeaderNames.SetCookie));

        MimeMessage message = await GetMessageForRecipientAsync(
            ownerEmail,
            timeout.Token);
        Assert.Equal(MailKitEmailVerificationDelivery.Subject, message.Subject);
        Assert.IsType<MultipartAlternative>(message.Body);
        string textBody = Assert.IsType<string>(message.TextBody);
        string htmlBody = Assert.IsType<string>(message.HtmlBody);
        MatchCollection verificationUrls = VerificationUrlPattern.Matches(textBody);
        Assert.Single(verificationUrls);
        Assert.True(Uri.TryCreate(
            verificationUrls[0].Value,
            UriKind.Absolute,
            out Uri? parsedVerificationUri));
        Uri verificationUri = Assert.IsType<Uri>(parsedVerificationUri);

        Assert.True(verificationUri.IsLoopback);
        Assert.Equal(Uri.UriSchemeHttp, verificationUri.Scheme);
        Assert.Equal("localhost", verificationUri.Host);
        Assert.Equal(5173, verificationUri.Port);
        Assert.Equal("/verify-email", verificationUri.AbsolutePath);
        Assert.Equal(string.Empty, verificationUri.Query);
        Assert.StartsWith("#token=", verificationUri.Fragment, StringComparison.Ordinal);

        string rawToken = verificationUri.Fragment["#token=".Length..];
        Assert.Matches("^[A-Za-z0-9_-]{43}$", rawToken);
        Assert.Contains($"#token={rawToken}", htmlBody);
        Assert.DoesNotContain(rawToken, message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            message.Headers,
            header => header.Value.Contains(rawToken, StringComparison.Ordinal));

        LogEntry deliveryLog = Assert.Single(
            loggerProvider.Entries,
            entry => entry.EventId.Id == 2000
                && string.Equals(
                    entry.Category,
                    typeof(MailKitEmailVerificationDelivery).FullName,
                    StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, deliveryLog.Level);
        Assert.Null(deliveryLog.Exception);
        Assert.DoesNotContain(
            loggerProvider.Entries,
            entry => entry.EventId.Id == 2003
                || entry.Message.Contains(rawToken, StringComparison.Ordinal)
                || (entry.Exception?.ToString().Contains(
                    rawToken,
                    StringComparison.Ordinal) ?? false));

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            User user = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == onboarding.UserId,
                    timeout.Token);
            EmailVerificationChallenge challenge = await dbContext
                .EmailVerificationChallenges
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.UserId == onboarding.UserId,
                    timeout.Token);
            IEmailVerificationTokenService tokenService = application.Services
                .GetRequiredService<IEmailVerificationTokenService>();

            Assert.Null(user.EmailVerifiedAt);
            Assert.True(tokenService.TryHashToken(rawToken, out var tokenHash));
            Assert.Equal(challenge.TokenHash, tokenHash);
            Assert.Equal(
                ["TokenHash"],
                typeof(EmailVerificationChallenge)
                    .GetProperties()
                    .Where(property => property.Name.Contains(
                        "Token",
                        StringComparison.Ordinal))
                    .Select(property => property.Name)
                    .ToArray());
        }

        using HttpResponseMessage verifyResponse = await httpClient
            .PostAsJsonAsync(
                VerifyPath,
                new { Token = rawToken },
                timeout.Token);

        Assert.Equal(HttpStatusCode.NoContent, verifyResponse.StatusCode);
        Assert.True(verifyResponse.Headers.CacheControl?.NoStore);

        using HttpResponseMessage postVerificationLogin = await httpClient
            .PostAsJsonAsync(
                LoginPath,
                new { Email = ownerEmail, Password },
                timeout.Token);

        Assert.Equal(HttpStatusCode.NoContent, postVerificationLogin.StatusCode);
        Assert.True(postVerificationLogin.Headers.CacheControl?.NoStore);
        Assert.True(postVerificationLogin.Headers.Contains(HeaderNames.SetCookie));

        await using EnmaDbContext verifiedDbContext = fixture.CreateDbContext();
        User verifiedUser = await verifiedDbContext.Users
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.Id == onboarding.UserId,
                timeout.Token);
        Assert.NotNull(verifiedUser.EmailVerifiedAt);
        Assert.Equal(
            0,
            await verifiedDbContext.EmailVerificationChallenges.CountAsync(
                candidate => candidate.UserId == onboarding.UserId,
                timeout.Token));
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

    public async Task DisposeAsync()
    {
        client?.Dispose();

        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        loggerProvider.Dispose();
        await mailpit.DisposeAsync();
    }

    private sealed class DevelopmentEnmaApiFactory(
        PostgreSqlFixture fixture,
        CapturingLoggerProvider loggerProvider,
        int smtpPort)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting(
                "ConnectionStrings:Database",
                fixture.ConnectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Database"] = fixture.ConnectionString
                    });
            });
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICompromisedPasswordChecker>();
                services.AddSingleton<ICompromisedPasswordChecker>(
                    new SafeCompromisedPasswordChecker());
                services.RemoveAll<DevelopmentEmailVerificationDelivery>();
                services.AddSingleton(serviceProvider =>
                    new DevelopmentEmailVerificationDelivery(
                        serviceProvider.GetRequiredService<IOptions<
                            DevelopmentEmailVerificationDeliveryOptions>>(),
                        serviceProvider.GetRequiredService<ILogger<
                            MailKitEmailVerificationDelivery>>(),
                        smtpPort));
            });
        }
    }

    private sealed class SafeCompromisedPasswordChecker
        : ICompromisedPasswordChecker
    {
        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, entries);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel != LogLevel.None;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new LogEntry(
                    category,
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception));
            }
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);
}
