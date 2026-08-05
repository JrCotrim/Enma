namespace Enma.Domain.Organizations;

public static class OrganizationMembershipErrors
{
    public const string OrganizationIdRequired = "Organization id cannot be empty.";
    public const string UserIdRequired = "User id cannot be empty.";
    public const string RoleInvalid = "Organization role is invalid.";
    public const string CreatedAtInvalid = "Organization membership creation date must be a valid value.";
}
