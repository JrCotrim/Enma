using Enma.Application.Organizations.Invitations;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentOrganizationInvitationDelivery
    : IOrganizationInvitationDelivery
{
    public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
        OrganizationInvitationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(OrganizationInvitationDeliveryResult.Failed);
    }
}
