using Enma.Application.Organizations.Invitations;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentOrganizationInvitationDelivery
    : IOrganizationInvitationDelivery
{
    private const int MailpitSmtpPort = 1025;

    private readonly MailKitOrganizationInvitationDelivery delivery;

    public DevelopmentOrganizationInvitationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<MailKitOrganizationInvitationDelivery> logger)
        : this(options, logger, MailpitSmtpPort)
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
                SenderName = "ENMA Development",
                SenderAddress = "no-reply@enma.local",
                SmtpHost = "127.0.0.1",
                SmtpPort = smtpPort,
                SmtpSecurity = SecureSocketOptions.None
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
}
