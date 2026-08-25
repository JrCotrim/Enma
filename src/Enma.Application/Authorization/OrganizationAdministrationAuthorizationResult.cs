using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAdministrationAuthorizationResult
{
    private OrganizationAdministrationAuthorizationResult(
        OrganizationAdministrationAuthorizationStatus status,
        OrganizationRole? role)
    {
        Status = status;
        Role = role;
    }

    public OrganizationAdministrationAuthorizationStatus Status { get; }

    public OrganizationRole? Role { get; }

    public static OrganizationAdministrationAuthorizationResult Denied { get; } =
        new(OrganizationAdministrationAuthorizationStatus.Denied, null);

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
            _ => false
        };
    }

    public static OrganizationAdministrationAuthorizationResult Allowed(
        OrganizationRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return new OrganizationAdministrationAuthorizationResult(
            OrganizationAdministrationAuthorizationStatus.Allowed,
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
    ViewTeamAdministrationDetails = 2
}
