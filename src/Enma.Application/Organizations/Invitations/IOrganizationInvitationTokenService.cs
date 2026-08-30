using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public interface IOrganizationInvitationTokenService
{
    string GenerateToken(out OrganizationInvitationTokenHash tokenHash);

    bool TryHashToken(
        string? rawToken,
        out OrganizationInvitationTokenHash? tokenHash);
}
