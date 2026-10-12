using Enma.Application.Organizations.Invitations;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentOrganizationInvitationDelivery
    : IOrganizationInvitationDelivery
{
    private readonly MailKitOrganizationInvitationDelivery delivery;

    public DevelopmentOrganizationInvitationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitOrganizationInvitationDelivery> logger)
        : this(options, logger, GetSmtpPort(options))
    {
    }

    internal DevelopmentOrganizationInvitationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitOrganizationInvitationDelivery> logger,
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
                SenderName = options.Value.SenderName,
                SenderAddress = "no-reply@enma.local",
                SmtpHost = options.Value.SmtpHost,
                SmtpPort = smtpPort,
                SmtpSecurity = SecureSocketOptions.None,
                SmtpTimeoutSeconds = options.Value.SmtpTimeoutSeconds
            });
        delivery = new MailKitOrganizationInvitationDelivery(
            deliveryOptions,
            new OrganizationInvitationLinkBuilder(deliveryOptions),
            logger);
    }

    public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
        OrganizationInvitationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return delivery.DeliverAsync(request, cancellationToken);
    }

    private static int GetSmtpPort(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Value.SmtpPort;
    }
}
