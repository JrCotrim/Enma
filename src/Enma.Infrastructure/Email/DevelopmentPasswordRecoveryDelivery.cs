using Enma.Application.Authentication;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentPasswordRecoveryDelivery : IPasswordRecoveryDelivery
{
    private readonly MailKitPasswordRecoveryDelivery delivery;

    public DevelopmentPasswordRecoveryDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitPasswordRecoveryDelivery> logger)
    {
        IOptions<EmailVerificationDeliveryOptions> deliveryOptions = Options.Create(
            new EmailVerificationDeliveryOptions
            {
                VerificationPageUrl = options.Value.VerificationPageUrl,
                PasswordRecoveryPageUrl = options.Value.PasswordRecoveryPageUrl,
                SenderName = "ENMA Development",
                SenderAddress = "no-reply@enma.local",
                SmtpHost = "127.0.0.1",
                SmtpPort = 1025,
                SmtpSecurity = SecureSocketOptions.None
            });
        delivery = new MailKitPasswordRecoveryDelivery(
            deliveryOptions,
            new PasswordRecoveryLinkBuilder(deliveryOptions),
            logger);
    }

    public Task<PasswordRecoveryDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default) =>
        delivery.DeliverAsync(email, rawToken, cancellationToken);
}
