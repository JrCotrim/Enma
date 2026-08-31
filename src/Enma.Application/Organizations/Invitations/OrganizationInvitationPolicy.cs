namespace Enma.Application.Organizations.Invitations;

public static class OrganizationInvitationPolicy
{
    public static TimeSpan TokenLifetime { get; } = TimeSpan.FromDays(7);

    public static TimeSpan ResendCooldown { get; } = TimeSpan.FromSeconds(60);
}
