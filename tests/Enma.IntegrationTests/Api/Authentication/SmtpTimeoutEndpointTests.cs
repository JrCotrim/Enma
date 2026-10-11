using System.Net;
using System.Net.Http.Json;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Security;
using Enma.Infrastructure.Email;
using Enma.IntegrationTests.Infrastructure.Email;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Enma.IntegrationTests.Api.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class SmtpTimeoutEndpointTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private const string OnboardingPath = "/api/onboarding/register";
    private const string OwnerEmail = "timeout-owner@example.test";
    private const string SyntheticPassword = "HttpTest!Owner42";
    private const string SmtpUsername = "synthetic-smtp-user";
    private const string SmtpPassword = "synthetic-smtp-password";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Timeout = 60_000)]
    public async Task Onboarding_SmtpTimesOut_KeepsRegistrationResponseWithoutLeakingData()
    {
        await using var smtp = SmtpTimeoutDeliveryTests.SilentSmtpServer.Start();
        var logs = new CapturingLoggerProvider();
        await using var factory = new EnmaApiFactory(fixture);
        await using WebApplicationFactory<Program> application =
            factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    string section = EmailVerificationDeliveryOptions.SectionName;
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            [$"{section}:VerificationPageUrl"] =
                                "https://app.example.test/verify-email",
                            [$"{section}:PasswordRecoveryPageUrl"] =
                                "https://app.example.test/reset-password",
                            [$"{section}:SenderName"] = "ENMA",
                            [$"{section}:SenderAddress"] = "no-reply@example.test",
                            [$"{section}:SmtpHost"] = "127.0.0.1",
                            [$"{section}:SmtpPort"] = smtp.Port.ToString(),
                            [$"{section}:SmtpSecurity"] = "StartTls",
                            [$"{section}:SmtpUsername"] = SmtpUsername,
                            [$"{section}:SmtpPassword"] = SmtpPassword,
                            [$"{section}:SmtpTimeoutSeconds"] = "1"
                        });
                });
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICompromisedPasswordChecker>();
                    services.AddSingleton<ICompromisedPasswordChecker>(
                        new SafeCompromisedPasswordChecker());
                });
            });
        using HttpClient client = application.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            OnboardingPath,
            new RegisterOrganizationOwnerRequest
            {
                OrganizationName = "Timeout Legal",
                OrganizationSlug = "timeout-legal",
                OwnerName = "Timeout Owner",
                OwnerEmail = OwnerEmail,
                Password = SyntheticPassword
            },
            smtp.TestTimeout);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterOrganizationOwnerResponse? registration = await response.Content
            .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>(smtp.TestTimeout);
        Assert.NotNull(registration);
        Assert.False(registration.VerificationEmailSent);

        CapturedLogEntry failure = Assert.Single(
            logs.Entries,
            entry => entry.EventId.Id == 2001);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Null(failure.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        foreach (string sensitiveValue in new[]
        {
            OwnerEmail,
            SyntheticPassword,
            SmtpUsername,
            SmtpPassword,
            "#token="
        })
        {
            Assert.DoesNotContain(
                logs.Entries,
                entry => entry.Contains(sensitiveValue));
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
}
