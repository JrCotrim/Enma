namespace Enma.Application.Authorization;

public sealed class LegalTaskViewAuthorizationResult
{
    private LegalTaskViewAuthorizationResult(
        LegalTaskViewAuthorizationStatus status,
        Guid? userId,
        Guid? organizationId,
        Guid? membershipId)
    {
        Status = status;
        UserId = userId;
        OrganizationId = organizationId;
        MembershipId = membershipId;
    }

    public LegalTaskViewAuthorizationStatus Status { get; }

    public Guid? UserId { get; }

    public Guid? OrganizationId { get; }

    public Guid? MembershipId { get; }

    public static LegalTaskViewAuthorizationResult Denied { get; } = new(
        LegalTaskViewAuthorizationStatus.Denied,
        null,
        null,
        null);

    public static LegalTaskViewAuthorizationResult Allowed(
        Guid userId,
        Guid organizationId,
        Guid membershipId)
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

        return new LegalTaskViewAuthorizationResult(
            LegalTaskViewAuthorizationStatus.Allowed,
            userId,
            organizationId,
            membershipId);
    }
}

public enum LegalTaskViewAuthorizationStatus
{
    Denied = 0,
    Allowed = 1
}
