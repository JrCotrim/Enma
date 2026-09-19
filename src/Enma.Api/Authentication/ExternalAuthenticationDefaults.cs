namespace Enma.Api.Authentication;

internal static class ExternalAuthenticationDefaults
{
    internal const string CookieScheme = "EnmaExternal";
    internal const string GoogleScheme = "Google";
    internal const string GoogleProvider = "Google";
    internal const string GoogleSubjectClaim = "sub";
    internal const string GoogleEmailClaim = "email";
    internal const string GoogleNameClaim = "name";
    internal const string GoogleEmailVerifiedClaim = "email_verified";
    internal const string InvitationItem = "enma:invitation";
}

internal sealed record GoogleAuthenticationAvailability(
    bool Enabled,
    Uri? FrontendOrigin);
