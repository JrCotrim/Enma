using System.Net;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptionsValidator
    : IValidateOptions<DevelopmentEmailVerificationDeliveryOptions>
{
    private const int MaximumVerificationPageUrlLength = 2_048;

    public ValidateOptionsResult Validate(
        string? name,
        DevelopmentEmailVerificationDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidatePageUrl(
            options.VerificationPageUrl,
            nameof(options.VerificationPageUrl),
            "/verify-email",
            failures);
        ValidatePageUrl(
            options.PasswordRecoveryPageUrl,
            nameof(options.PasswordRecoveryPageUrl),
            "/reset-password",
            failures);
        if (string.IsNullOrWhiteSpace(options.SenderName) ||
            options.SenderName.Any(char.IsControl))
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:SenderName is required and must not contain control characters.");
        }
        if (!IsLoopbackOrSingleLabelSmtpHost(options.SmtpHost))
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:SmtpHost must be a loopback address or a single-label host name.");
        }
        if (options.SmtpPort is < 1 or > 65_535)
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:SmtpPort must be between 1 and 65535.");
        }
        if (options.SmtpTimeoutSeconds is
            < EmailVerificationDeliveryOptions.MinimumSmtpTimeoutSeconds
            or > EmailVerificationDeliveryOptions.MaximumSmtpTimeoutSeconds)
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:SmtpTimeoutSeconds must be between {EmailVerificationDeliveryOptions.MinimumSmtpTimeoutSeconds} and {EmailVerificationDeliveryOptions.MaximumSmtpTimeoutSeconds}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePageUrl(
        string value,
        string optionName,
        string expectedPath,
        ICollection<string> failures)
    {

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumVerificationPageUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !IsLoopbackHost(uri)
            || (!string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)
                && !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.Ordinal))
            || !string.Equals(
                uri.AbsolutePath,
                expectedPath,
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:{optionName} must be an absolute HTTP(S) loopback URI for {expectedPath} without a query, fragment, or user information.");
        }
    }

    // Development mail is unauthenticated and unencrypted, so it may target
    // only this computer or a single-label name (no dot, not an IP address)
    // that resolves on a private Compose network.
    private static bool IsLoopbackOrSingleLabelSmtpHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Also catches numeric forms such as "2130706433" that resolvers
        // read as an IPv4 address.
        if (IPAddress.TryParse(value, out IPAddress? address))
        {
            return IPAddress.IsLoopback(address);
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns &&
            !value.Contains('.', StringComparison.Ordinal);
    }

    // RFC 6761 reserves every *.localhost name for loopback and browsers
    // resolve them to it, so a dedicated local hostname (the Pilot's
    // enma-pilot.localhost) keeps the links on this computer.
    private static bool IsLoopbackHost(Uri uri)
    {
        const string LocalhostSuffix = ".localhost";

        return uri.IsLoopback ||
            (uri.HostNameType == UriHostNameType.Dns &&
                uri.IdnHost.Length > LocalhostSuffix.Length &&
                uri.IdnHost.EndsWith(
                    LocalhostSuffix,
                    StringComparison.OrdinalIgnoreCase));
    }
}
