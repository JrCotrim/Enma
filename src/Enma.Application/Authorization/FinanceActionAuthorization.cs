using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class FinanceActionAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public FinanceActionAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<FinanceActionAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        FinanceAction action,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            organizationId == Guid.Empty ||
            !Enum.IsDefined(action))
        {
            return FinanceActionAuthorizationResult.Denied;
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
            return FinanceActionAuthorizationResult.Denied;
        }

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return FinanceActionAuthorizationResult.Denied;
        }

        return CanExecute(action, role)
            ? FinanceActionAuthorizationResult.Allowed
            : FinanceActionAuthorizationResult.Denied;
    }

    internal async Task<OrganizationAccessAuthorizationResult> AuthorizeActorAsync(
        Guid userId,
        Guid organizationId,
        FinanceAction action,
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

    internal bool CanExecute(FinanceAction action, OrganizationRole role)
    {
        return (action, role) switch
        {
            (FinanceAction.View or
                FinanceAction.CreatePaymentPlan or
                FinanceAction.MarkInstallmentPaid,
                OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            _ => false
        };
    }
}
