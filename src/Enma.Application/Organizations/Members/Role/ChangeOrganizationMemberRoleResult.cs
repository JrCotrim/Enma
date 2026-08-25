namespace Enma.Application.Organizations.Members.Role;

public enum ChangeOrganizationMemberRoleResult
{
    AccessDenied = 0,
    NotFound = 1,
    TargetForbidden = 2,
    Conflict = 3,
    Succeeded = 4
}
