namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptions
{
    public const string SectionName = "EmailVerification:DevelopmentDelivery";

    public const string DefaultVerificationPageUrl =
        "http://localhost:5173/verify-email";

    public string VerificationPageUrl { get; init; } =
        DefaultVerificationPageUrl;
}
