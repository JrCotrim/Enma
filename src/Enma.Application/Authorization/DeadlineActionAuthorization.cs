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

        return (action, role) switch
        {
            (DeadlineAction.View, OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => DeadlineActionAuthorizationResult.Allowed,
            (DeadlineAction.Create, OrganizationRole.Owner or
                OrganizationRole.Administrator) => DeadlineActionAuthorizationResult.Allowed,
            (DeadlineAction.Update or
                DeadlineAction.Complete or
                DeadlineAction.Reopen, OrganizationRole.Owner or
                OrganizationRole.Administrator) => DeadlineActionAuthorizationResult.Allowed,
            _ => DeadlineActionAuthorizationResult.Denied
        };
    }
}
