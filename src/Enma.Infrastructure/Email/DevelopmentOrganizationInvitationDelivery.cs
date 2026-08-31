using Enma.Application.Organizations.Invitations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentOrganizationInvitationDelivery
    : IOrganizationInvitationDelivery
{
    private static readonly Action<ILogger, string, Exception?> LogInvitationUrl =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2013, "DevelopmentOrganizationInvitationUrl"),
            "DEVELOPMENT ONLY - accept the organization invitation with this local URL: {InvitationUrl}");

    private readonly OrganizationInvitationLinkBuilder linkBuilder;
    private readonly ILogger<DevelopmentOrganizationInvitationDelivery> logger;

    public DevelopmentOrganizationInvitationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<DevelopmentOrganizationInvitationDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        linkBuilder = new OrganizationInvitationLinkBuilder(new Uri(
            options.Value.VerificationPageUrl,
            UriKind.Absolute));
        this.logger = logger;
    }

    public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
        OrganizationInvitationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        cancellationToken.ThrowIfCancellationRequested();

        Uri invitationUri = linkBuilder.Build(request.RawToken);
        LogInvitationUrl(logger, invitationUri.AbsoluteUri, null);

        return Task.FromResult(OrganizationInvitationDeliveryResult.Accepted);
    }
}
