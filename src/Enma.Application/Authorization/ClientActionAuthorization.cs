using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class ClientActionAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public ClientActionAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<ClientActionAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        ClientAction action,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            !Enum.IsDefined(action))
        {
            return ClientActionAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess =
            await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return ClientActionAuthorizationResult.Denied;
        }

        return (action, role) switch
        {
            (ClientAction.View, OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => ClientActionAuthorizationResult.Allowed,
            (ClientAction.Create, OrganizationRole.Owner or
                OrganizationRole.Administrator) => ClientActionAuthorizationResult.Allowed,
            _ => ClientActionAuthorizationResult.Denied
        };
    }
}
