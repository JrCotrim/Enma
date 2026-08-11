namespace Enma.Api.Endpoints.Authentication;

internal static class AuthenticationCookies
{
    private const string RootPath = "/";

    internal const string SessionName = "__Host-enma_session";
    internal const string AntiforgeryName = "__Host-enma_csrf";

    internal static void ConfigureAntiforgery(CookieBuilder cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);

        cookie.Name = AntiforgeryName;
        cookie.Path = RootPath;
        cookie.HttpOnly = true;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.SameSite = SameSiteMode.Strict;
    }

    internal static CookieOptions CreateSessionOptions()
    {
        return new CookieOptions
        {
            Path = RootPath,
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax
        };
    }

    internal static CookieOptions CreateAntiforgeryOptions()
    {
        return new CookieOptions
        {
            Path = RootPath,
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict
        };
    }
}
