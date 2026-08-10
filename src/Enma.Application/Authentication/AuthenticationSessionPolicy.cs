namespace Enma.Application.Authentication;

public static class AuthenticationSessionPolicy
{
    public static TimeSpan IdleLifetime { get; } = TimeSpan.FromMinutes(30);

    public static TimeSpan AbsoluteLifetime { get; } = TimeSpan.FromHours(12);
}
