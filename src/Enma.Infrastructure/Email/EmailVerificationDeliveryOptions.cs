using MailKit.Security;

namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationDeliveryOptions
{
    public const string SectionName = "EmailVerification:Delivery";

    public const int DefaultSmtpTimeoutSeconds = 10;

    public const int MinimumSmtpTimeoutSeconds = 1;

    public const int MaximumSmtpTimeoutSeconds = 60;

    public string VerificationPageUrl { get; init; } = string.Empty;

    public string PasswordRecoveryPageUrl { get; init; } = string.Empty;

    public string SenderName { get; init; } = string.Empty;

    public string SenderAddress { get; init; } = string.Empty;

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; }

    public SecureSocketOptions SmtpSecurity { get; init; }

    public string SmtpUsername { get; init; } = string.Empty;

    public string SmtpPassword { get; init; } = string.Empty;

    public int SmtpTimeoutSeconds { get; init; } = DefaultSmtpTimeoutSeconds;
}
