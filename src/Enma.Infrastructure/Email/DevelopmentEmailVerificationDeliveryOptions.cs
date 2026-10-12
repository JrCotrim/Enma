namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptions
{
    public const string SectionName = "EmailVerification:DevelopmentDelivery";

    public const string DefaultVerificationPageUrl =
        "http://localhost:5173/verify-email";

    public const string DefaultPasswordRecoveryPageUrl =
        "http://localhost:5173/reset-password";

    public const string DefaultSmtpHost = "127.0.0.1";

    public string VerificationPageUrl { get; init; } =
        DefaultVerificationPageUrl;

    public string PasswordRecoveryPageUrl { get; init; } =
        DefaultPasswordRecoveryPageUrl;

    public string SenderName { get; init; } = "ENMA Development";

    // Loopback or a single-label host (the Pilot's Mailpit service name on
    // its internal Compose network); see the validator.
    public string SmtpHost { get; init; } = DefaultSmtpHost;

    public int SmtpPort { get; init; } = 1025;

    public int SmtpTimeoutSeconds { get; init; } =
        EmailVerificationDeliveryOptions.DefaultSmtpTimeoutSeconds;
}
