using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationDeliveryOptionsValidator
    : IValidateOptions<EmailVerificationDeliveryOptions>
{
    private const int MaximumVerificationPageUrlLength = 2_048;
    private const int MaximumSenderNameLength = 200;
    private const int MaximumSmtpHostLength = 253;

    public ValidateOptionsResult Validate(
        string? name,
        EmailVerificationDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateVerificationPageUrl(options.VerificationPageUrl, failures);
        ValidateSenderName(options.SenderName, failures);
        ValidateSenderAddress(options.SenderAddress, failures);
        ValidateSmtpHost(options.SmtpHost, failures);

        if (options.SmtpPort is < 1 or > 65_535)
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SmtpPort must be between 1 and 65535.");
        }

        if (options.SmtpSecurity is not SecureSocketOptions.StartTls
            and not SecureSocketOptions.SslOnConnect)
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SmtpSecurity must require TLS.");
        }

        if (string.IsNullOrWhiteSpace(options.SmtpUsername))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SmtpUsername is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SmtpPassword))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SmtpPassword is required.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateVerificationPageUrl(
        string value,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl is required.");
            return;
        }

        if (value.Length > MaximumVerificationPageUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || string.IsNullOrEmpty(uri.Host))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl must be a reasonable absolute HTTPS URI.");
            return;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl must not contain a query.");
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl must not contain a fragment.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl must not contain user information.");
        }
    }

    private static void ValidateSenderName(
        string value,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumSenderNameLength
            || value.Any(char.IsControl))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SenderName is required and must be a reasonable header value.");
        }
    }

    private static void ValidateSenderAddress(
        string value,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !MailboxAddress.TryParse(value, out MailboxAddress? mailbox)
            || !string.Equals(mailbox.Address, value, StringComparison.OrdinalIgnoreCase)
            || !HasValidMailboxHost(mailbox.Address))
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SenderAddress must be a valid mailbox address.");
        }
    }

    private static bool HasValidMailboxHost(string address)
    {
        int separatorIndex = address.LastIndexOf('@');

        return separatorIndex > 0
            && separatorIndex < address.Length - 1
            && Uri.CheckHostName(address[(separatorIndex + 1)..])
                != UriHostNameType.Unknown;
    }

    private static void ValidateSmtpHost(
        string value,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumSmtpHostLength
            || value.Any(char.IsWhiteSpace)
            || value.Contains("://", StringComparison.Ordinal)
            || value.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0
            || Uri.CheckHostName(value) == UriHostNameType.Unknown)
        {
            failures.Add(
                $"{EmailVerificationDeliveryOptions.SectionName}:SmtpHost must contain a valid host only.");
        }
    }
}
