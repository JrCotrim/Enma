namespace Enma.Application.Organizations.Members.Role;

public sealed record ChangeOrganizationMemberRoleCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    string? Role,
    string? ExpectedCurrentRole);
