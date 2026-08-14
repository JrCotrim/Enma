using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class LegalTaskViewAuthorization
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;

    public LegalTaskViewAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<LegalTaskViewAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
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
            return LegalTaskViewAuthorizationResult.Denied;
        }

        if (organizationAccess.Status != OrganizationAccessAuthorizationStatus.Allowed ||
            organizationAccess.UserId != userId ||
            organizationAccess.OrganizationId != organizationId ||
            organizationAccess.MembershipId is not Guid membershipId ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return LegalTaskViewAuthorizationResult.Denied;
        }

        return role switch
        {
            OrganizationRole.Owner or
            OrganizationRole.Administrator or
            OrganizationRole.Member => LegalTaskViewAuthorizationResult.Allowed(
                organizationAccess.UserId.Value,
                organizationAccess.OrganizationId.Value,
                membershipId),
            _ => LegalTaskViewAuthorizationResult.Denied
        };
    }
}
