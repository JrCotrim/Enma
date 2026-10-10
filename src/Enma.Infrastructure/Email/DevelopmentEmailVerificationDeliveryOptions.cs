namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptions
{
    public const string SectionName = "EmailVerification:DevelopmentDelivery";

    public const string DefaultVerificationPageUrl =
        "http://localhost:5173/verify-email";

    public const string DefaultPasswordRecoveryPageUrl =
        "http://localhost:5173/reset-password";

    public string VerificationPageUrl { get; init; } =
        DefaultVerificationPageUrl;

    public string PasswordRecoveryPageUrl { get; init; } =
        DefaultPasswordRecoveryPageUrl;

    public string SenderName { get; init; } = "ENMA Development";

    public int SmtpPort { get; init; } = 1025;
}
