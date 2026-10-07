namespace Enma.Domain.Organizations;

public static class OrganizationMembershipErrors
{
    public const string OrganizationIdRequired = "Organization id cannot be empty.";
    public const string UserIdRequired = "User id cannot be empty.";
    public const string RoleInvalid = "Organization role is invalid.";
    public const string CreatedAtInvalid = "Organization membership creation date must be a valid value.";
    public const string OwnerDemotionInvalidState =
        "Only an active Owner membership can be demoted to Administrator.";
    public const string OwnerPromotionInvalidState =
        "Only an active Administrator membership can be promoted to Owner.";
}
