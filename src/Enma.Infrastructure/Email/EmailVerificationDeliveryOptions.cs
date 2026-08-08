using MailKit.Security;

namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationDeliveryOptions
{
    public const string SectionName = "EmailVerification:Delivery";

    public string VerificationPageUrl { get; init; } = string.Empty;

    public string SenderName { get; init; } = string.Empty;

    public string SenderAddress { get; init; } = string.Empty;

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; }

    public SecureSocketOptions SmtpSecurity { get; init; }

    public string SmtpUsername { get; init; } = string.Empty;

    public string SmtpPassword { get; init; } = string.Empty;
}
