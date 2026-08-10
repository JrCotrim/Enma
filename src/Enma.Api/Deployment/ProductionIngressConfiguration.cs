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

        if (IsEnabled(environmentShortcutValue) ||
            IsEnabled(configuredShortcutValue) ||
            allowedHosts.Length == 0 ||
            allowedHosts.Any(host => host == "*"))
        {
            throw new InvalidOperationException(ValidationError);
        }
    }

    private static bool IsEnabled(string? value)
    {
        return bool.TryParse(value, out bool enabled) && enabled;
    }
}
