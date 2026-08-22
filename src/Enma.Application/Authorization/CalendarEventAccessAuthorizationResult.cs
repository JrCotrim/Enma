using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class CalendarEventAccessAuthorizationResult
{
    private CalendarEventAccessAuthorizationResult(
        CalendarEventAccessAuthorizationStatus status,
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

    public CalendarEventAccessAuthorizationStatus Status { get; }

    public Guid? UserId { get; }

    public Guid? OrganizationId { get; }

    public Guid? MembershipId { get; }

    public OrganizationRole? Role { get; }

    public static CalendarEventAccessAuthorizationResult Denied { get; } = new(
        CalendarEventAccessAuthorizationStatus.Denied,
        null,
        null,
        null,
        null);

    public static CalendarEventAccessAuthorizationResult Allowed(
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

        return new CalendarEventAccessAuthorizationResult(
            CalendarEventAccessAuthorizationStatus.Allowed,
            userId,
            organizationId,
            membershipId,
            role);
    }
}

public enum CalendarEventAccessAuthorizationStatus
{
    Denied = 0,
    Allowed = 1
}
