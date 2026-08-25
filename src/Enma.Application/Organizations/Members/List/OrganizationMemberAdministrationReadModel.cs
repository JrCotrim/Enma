using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Members.List;

public sealed record OrganizationMemberAdministrationReadModel(
    Guid Id,
    string Name,
    string? Email,
    OrganizationRole Role,
    OrganizationMembershipStatus? MembershipStatus,
    OrganizationAccountStatus? AccountStatus);

public enum OrganizationMembershipStatus
{
    Active = 1,
    Inactive = 2
}

public enum OrganizationAccountStatus
{
    Active = 1,
    Inactive = 2
}
