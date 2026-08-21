using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class DevelopmentOwnerOnboardingEmailVerificationEndToEndTests
    : IAsyncLifetime
{
    private const string OnboardingPath = "/api/onboarding/register";
    private const string LoginPath = "/api/auth/login";
    private const string VerifyPath = "/api/auth/email-verification/verify";
    private const string Password = "Development!Owner42";
    private const string DevelopmentLogPrefix =
        "DEVELOPMENT ONLY - verify the email with this local URL: ";

    private readonly PostgreSqlFixture fixture;
    private readonly CapturingLoggerProvider loggerProvider = new();
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
        factory = new DevelopmentEnmaApiFactory(fixture, loggerProvider);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task DevelopmentOnboarding_LoggedFragment_VerifiesBeforeLoginSucceeds()
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

        LogEntry deliveryLog = Assert.Single(
            loggerProvider.Entries,
            entry => entry.EventId.Id == 2003
                && string.Equals(
                    entry.Category,
                    typeof(DevelopmentEmailVerificationDelivery).FullName,
                    StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, deliveryLog.Level);
        Assert.Null(deliveryLog.Exception);
        Assert.StartsWith(
            DevelopmentLogPrefix,
            deliveryLog.Message,
            StringComparison.Ordinal);
        Assert.True(Uri.TryCreate(
            deliveryLog.Message[DevelopmentLogPrefix.Length..],
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

    public async Task DisposeAsync()
    {
        client?.Dispose();

        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        loggerProvider.Dispose();
    }

    private sealed class DevelopmentEnmaApiFactory(
        PostgreSqlFixture fixture,
        CapturingLoggerProvider loggerProvider)
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
