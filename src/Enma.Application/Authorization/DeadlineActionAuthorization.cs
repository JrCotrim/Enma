using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class DeadlineActionAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public DeadlineActionAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<DeadlineActionAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        DeadlineAction action,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            !Enum.IsDefined(action))
        {
            return DeadlineActionAuthorizationResult.Denied;
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
            return DeadlineActionAuthorizationResult.Denied;
        }

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return DeadlineActionAuthorizationResult.Denied;
        }

        return CanExecute(action, role)
            ? DeadlineActionAuthorizationResult.Allowed
            : DeadlineActionAuthorizationResult.Denied;
    }

    internal async Task<OrganizationAccessAuthorizationResult> AuthorizeActorAsync(
        Guid userId,
        Guid organizationId,
        DeadlineAction action,
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

    internal bool CanExecute(DeadlineAction action, OrganizationRole role)
    {
        return (action, role) switch
        {
            (DeadlineAction.View, OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => true,
            (DeadlineAction.Create, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (DeadlineAction.Update or
                DeadlineAction.Complete or
                DeadlineAction.Reopen, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            _ => false
        };
    }
}
