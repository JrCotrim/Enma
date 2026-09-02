using Enma.Application.Authentication;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDelivery
    : IEmailVerificationDelivery
{
    private const int MailpitSmtpPort = 1025;

    private readonly MailKitEmailVerificationDelivery delivery;

    public DevelopmentEmailVerificationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitEmailVerificationDelivery> logger)
        : this(options, logger, MailpitSmtpPort)
    {
    }

    internal DevelopmentEmailVerificationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitEmailVerificationDelivery> logger,
        int smtpPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(smtpPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(smtpPort, 65_535);

        IOptions<EmailVerificationDeliveryOptions> deliveryOptions =
            Options.Create(new EmailVerificationDeliveryOptions
            {
                VerificationPageUrl = options.Value.VerificationPageUrl,
                SenderName = "ENMA Development",
                SenderAddress = "no-reply@enma.local",
                SmtpHost = "127.0.0.1",
                SmtpPort = smtpPort,
                SmtpSecurity = SecureSocketOptions.None
            });
        delivery = new MailKitEmailVerificationDelivery(
            deliveryOptions,
            new EmailVerificationLinkBuilder(deliveryOptions),
            logger);
    }

    public Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        return delivery.DeliverAsync(email, rawToken, cancellationToken);
    }
}
