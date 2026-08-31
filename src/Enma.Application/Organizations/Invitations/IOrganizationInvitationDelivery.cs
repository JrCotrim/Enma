using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public interface IOrganizationInvitationDelivery
{
    Task<OrganizationInvitationDeliveryResult> DeliverAsync(
        OrganizationInvitationDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationInvitationDeliveryRequest(
    string Email,
    string OrganizationName,
    OrganizationRole Role,
    DateTimeOffset ExpiresAt,
    string RawToken);

public enum OrganizationInvitationDeliveryResult
{
    Failed = 0,
    Accepted = 1
}
