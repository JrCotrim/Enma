namespace Enma.Application.Authentication;

public static class PasswordRecoveryPolicy
{
    public static TimeSpan TokenLifetime { get; } = TimeSpan.FromHours(1);

    public static TimeSpan ResendCooldown { get; } = TimeSpan.FromSeconds(60);
}
