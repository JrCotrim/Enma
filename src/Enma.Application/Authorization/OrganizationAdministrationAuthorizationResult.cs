using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAdministrationAuthorizationResult
{
    private OrganizationAdministrationAuthorizationResult(
        OrganizationAdministrationAuthorizationStatus status,
        Guid? userId,
        Guid? organizationId,
        Guid? membershipId,
        OrganizationRole? role)
    {
        Status = status;
        UserId = userId;
        OrganizationId = organizationId;
        MembershipId = membershipId;
        Role = role;
    }

    public OrganizationAdministrationAuthorizationStatus Status { get; }

    public Guid? UserId { get; }

    public Guid? OrganizationId { get; }

    public Guid? MembershipId { get; }

    public OrganizationRole? Role { get; }

    public static OrganizationAdministrationAuthorizationResult Denied { get; } =
        new(
            OrganizationAdministrationAuthorizationStatus.Denied,
            null,
            null,
            null,
            null);

    public bool Allows(OrganizationAdministrationAction action)
    {
        if (Status != OrganizationAdministrationAuthorizationStatus.Allowed ||
            Role is not OrganizationRole role ||
            !Enum.IsDefined(action))
        {
            return false;
        }

        return (action, role) switch
        {
            (OrganizationAdministrationAction.ViewTeam,
                OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member) => true,
            (OrganizationAdministrationAction.ViewTeamAdministrationDetails,
                OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (OrganizationAdministrationAction.ViewAuditLog,
                OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            (OrganizationAdministrationAction.ChangeMemberRole,
                OrganizationRole.Owner) => true,
            (OrganizationAdministrationAction.EditOrganization,
                OrganizationRole.Owner) => true,
            (OrganizationAdministrationAction.DeactivateMember or
                OrganizationAdministrationAction.ReactivateMember,
                OrganizationRole.Owner or
                OrganizationRole.Administrator) => true,
            _ => false
        };
    }

    public static OrganizationAdministrationAuthorizationResult Allowed(
        Guid userId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id cannot be empty.", nameof(userId));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id cannot be empty.",
                nameof(organizationId));
        }

        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException(
                "Membership id cannot be empty.",
                nameof(membershipId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return new OrganizationAdministrationAuthorizationResult(
            OrganizationAdministrationAuthorizationStatus.Allowed,
            userId,
            organizationId,
            membershipId,
            role);
    }
}

public enum OrganizationAdministrationAuthorizationStatus
{
    Denied = 0,
    Allowed = 1
}

public enum OrganizationAdministrationAction
{
    ViewTeam = 1,
    ViewTeamAdministrationDetails = 2,
    ChangeMemberRole = 3,
    DeactivateMember = 4,
    ReactivateMember = 5,
    EditOrganization = 6,
    ViewAuditLog = 7
}
