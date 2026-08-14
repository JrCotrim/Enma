using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAccessAuthorizationResult
{
    private OrganizationAccessAuthorizationResult(
        OrganizationAccessAuthorizationStatus status,
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

    public OrganizationAccessAuthorizationStatus Status { get; }

    public Guid? UserId { get; }

    public Guid? OrganizationId { get; }

    public Guid? MembershipId { get; }

    public OrganizationRole? Role { get; }

    public static OrganizationAccessAuthorizationResult Denied { get; } = new(
        OrganizationAccessAuthorizationStatus.Denied,
        null,
        null,
        null,
        null);

    public static OrganizationAccessAuthorizationResult Allowed(
        OrganizationRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return new OrganizationAccessAuthorizationResult(
            OrganizationAccessAuthorizationStatus.Allowed,
            null,
            null,
            null,
            role);
    }

    public static OrganizationAccessAuthorizationResult Allowed(
        Guid userId,
        Guid organizationId,
        Guid? membershipId,
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
                "Membership id cannot be empty when supplied.",
                nameof(membershipId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return new OrganizationAccessAuthorizationResult(
            OrganizationAccessAuthorizationStatus.Allowed,
            userId,
            organizationId,
            membershipId,
            role);
    }
}

public enum OrganizationAccessAuthorizationStatus
{
    Denied = 0,
    Allowed = 1
}
