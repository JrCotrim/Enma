using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class OrganizationAccessAuthorizationResult
{
    private OrganizationAccessAuthorizationResult(
        OrganizationAccessAuthorizationStatus status,
        OrganizationRole? role)
    {
        Status = status;
        Role = role;
    }

    public OrganizationAccessAuthorizationStatus Status { get; }

    public OrganizationRole? Role { get; }

    public static OrganizationAccessAuthorizationResult Denied { get; } = new(
        OrganizationAccessAuthorizationStatus.Denied,
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
            role);
    }
}

public enum OrganizationAccessAuthorizationStatus
{
    Denied = 0,
    Allowed = 1
}
