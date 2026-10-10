using Enma.Application.Security;
using Enma.Infrastructure.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Enma.E2ETests.Infrastructure;

// "E2E" is neither Development nor Production nor Pilot: e-mail goes through
// the configurable MailKit delivery (no fixed Development SMTP port) and no
// Pilot setting is read. Rate limits and the TimeProvider stay real.
internal sealed class EnmaE2EApplicationFactory : WebApplicationFactory<Program>
{
    public const string EnvironmentName = "E2E";

    private readonly IReadOnlyDictionary<string, string?> settings;
    private readonly string distributionPath;
    private readonly string dataProtectionKeysPath;

    public EnmaE2EApplicationFactory(
        IReadOnlyDictionary<string, string?> settings,
        string distributionPath,
        string dataProtectionKeysPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataProtectionKeysPath);
        this.settings = settings;
        this.distributionPath = distributionPath;
        this.dataProtectionKeysPath = dataProtectionKeysPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        // Program reads some values before the host is built, so they are
        // provided both as host settings and as application configuration.
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(settings));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICompromisedPasswordChecker>();
            services.AddSingleton<
                ICompromisedPasswordChecker,
                FakeCompromisedPasswordChecker>();

            // Approved substitute (D1): the disposable Mailpit has no TLS and
            // the production validator requires it. The delivery chain itself
            // stays the production MailKit one.
            services.RemoveAll<
                IValidateOptions<EmailVerificationDeliveryOptions>>();

            // Outside Production and Pilot the API keeps the default key ring,
            // which lives in the user profile and is shared with Development
            // runs. Each E2E host gets its own throwaway key ring instead.
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

            services.AddSingleton<IStartupFilter>(
                new SpaStaticFilesStartupFilter(distributionPath));
        });
    }
}
