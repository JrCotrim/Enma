using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class ProcessActionAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public ProcessActionAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<ProcessActionAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        ProcessAction action,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            !Enum.IsDefined(action))
        {
            return ProcessActionAuthorizationResult.Denied;
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
            return ProcessActionAuthorizationResult.Denied;
        }

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return ProcessActionAuthorizationResult.Denied;
        }

        return CanExecute(action, role)
            ? ProcessActionAuthorizationResult.Allowed
            : ProcessActionAuthorizationResult.Denied;
    }

    internal async Task<OrganizationAccessAuthorizationResult> AuthorizeActorAsync(
        Guid userId,
        Guid organizationId,
        ProcessAction action,
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

    internal bool CanExecute(ProcessAction action, OrganizationRole role)
    {
        return (action, role) switch
        {
            (ProcessAction.View, OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => true,
            (ProcessAction.Create, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (ProcessAction.Update, OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            _ => false
        };
    }
}
