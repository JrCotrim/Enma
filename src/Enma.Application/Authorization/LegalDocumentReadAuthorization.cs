using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class LegalDocumentReadAuthorization
{
    private readonly OrganizationAccessAuthorization
        _organizationAccessAuthorization;

    public LegalDocumentReadAuthorization(
        OrganizationAccessAuthorization organizationAccessAuthorization)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        _organizationAccessAuthorization = organizationAccessAuthorization;
    }

    public async Task<LegalDocumentReadAuthorizationResult> AuthorizeAsync(
        Guid userId,
        Guid organizationId,
        LegalDocumentReadAction action,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action))
        {
            return LegalDocumentReadAuthorizationResult.Denied;
        }

        OrganizationAccessAuthorizationResult organizationAccess;

        try
        {
            organizationAccess =
                await _organizationAccessAuthorization.AuthorizeAsync(
                    userId,
                    organizationId,
                    cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return LegalDocumentReadAuthorizationResult.Denied;
        }

        if (organizationAccess.Status !=
                OrganizationAccessAuthorizationStatus.Allowed ||
            organizationAccess.UserId != userId ||
            organizationAccess.OrganizationId != organizationId ||
            organizationAccess.MembershipId is not Guid membershipId ||
            membershipId == Guid.Empty ||
            organizationAccess.Role is not OrganizationRole role)
        {
            return LegalDocumentReadAuthorizationResult.Denied;
        }

        return (action, role) switch
        {
            (LegalDocumentReadAction.ListMetadata,
                OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) =>
                LegalDocumentReadAuthorizationResult.Allowed,
            (LegalDocumentReadAction.ViewMetadata,
                OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) =>
                LegalDocumentReadAuthorizationResult.Allowed,
            _ => LegalDocumentReadAuthorizationResult.Denied
        };
    }
}
