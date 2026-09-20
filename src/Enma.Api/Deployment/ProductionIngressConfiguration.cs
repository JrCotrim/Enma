using Microsoft.Extensions.Hosting;

namespace Enma.Api.Deployment;

internal static class ProductionIngressConfiguration
{
    private const string ForwardedHeadersShortcut =
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED";
    private const string ValidationError =
        "Production ingress configuration is invalid.";

    public static void Validate(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsProduction())
        {
            return;
        }

        string? environmentShortcutValue =
            Environment.GetEnvironmentVariable(ForwardedHeadersShortcut);
        string? configuredShortcutValue = configuration[ForwardedHeadersShortcut];
        string? allowedHostsValue = configuration["AllowedHosts"];
        string[] allowedHosts = allowedHostsValue?.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries) ?? [];
        string? googleFrontendOriginValue =
            configuration["Authentication:Google:FrontendOrigin"];
        string? verificationPageUrl = configuration[
            "EmailVerification:Delivery:VerificationPageUrl"];
        string? passwordRecoveryPageUrl = configuration[
            "EmailVerification:Delivery:PasswordRecoveryPageUrl"];

        if (IsEnabled(environmentShortcutValue) ||
            IsEnabled(configuredShortcutValue) ||
            allowedHosts.Length == 0 ||
            allowedHosts.Any(host =>
                host.Contains('*', StringComparison.Ordinal) ||
                Uri.CheckHostName(host) == UriHostNameType.Unknown) ||
            !IsAuthorizedFrontendOrigin(
                googleFrontendOriginValue,
                allowedHosts) ||
            !HasAuthorizedHostIfConfigured(
                verificationPageUrl,
                allowedHosts) ||
            !HasAuthorizedHostIfConfigured(
                passwordRecoveryPageUrl,
                allowedHosts))
        {
            throw new InvalidOperationException(ValidationError);
        }
    }

    private static bool IsEnabled(string? value)
    {
        return bool.TryParse(value, out bool enabled) && enabled;
    }

    private static bool IsAuthorizedFrontendOrigin(
        string? configuredOrigin,
        IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(configuredOrigin))
        {
            return true;
        }

        return Uri.TryCreate(configuredOrigin, UriKind.Absolute, out Uri? origin) &&
            allowedHosts.Contains(
                origin.IdnHost,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasAuthorizedHostIfConfigured(
        string? configuredUrl,
        IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl) ||
            !Uri.TryCreate(configuredUrl, UriKind.Absolute, out Uri? uri))
        {
            return true;
        }

        return allowedHosts.Contains(
            uri.IdnHost,
            StringComparer.OrdinalIgnoreCase);
    }
}
