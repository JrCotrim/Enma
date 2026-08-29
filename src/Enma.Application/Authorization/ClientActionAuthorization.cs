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

        return CanExecute(action, role)
            ? ClientActionAuthorizationResult.Allowed
            : ClientActionAuthorizationResult.Denied;
    }

    internal async Task<OrganizationAccessAuthorizationResult> AuthorizeActorAsync(
        Guid userId,
        Guid organizationId,
        ClientAction action,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            !Enum.IsDefined(action))
        {
            return OrganizationAccessAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess;

        try
        {
            organizationAccess = await _organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return OrganizationAccessAuthorizationResult.Denied;
        }

        return organizationAccess.Status == OrganizationAccessAuthorizationStatus.Allowed &&
            organizationAccess.UserId == userId &&
            organizationAccess.OrganizationId == organizationId &&
            organizationAccess.MembershipId is Guid membershipId &&
            membershipId != Guid.Empty &&
            organizationAccess.Role is OrganizationRole role &&
            CanExecute(action, role)
                ? organizationAccess
                : OrganizationAccessAuthorizationResult.Denied;
    }

    internal bool CanExecute(ClientAction action, OrganizationRole role)
    {
        return (action, role) switch
        {
            (ClientAction.View, OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => true,
            (ClientAction.Create, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (ClientAction.Update, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (ClientAction.Deactivate, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (ClientAction.Reactivate, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            _ => false
        };
    }
}
