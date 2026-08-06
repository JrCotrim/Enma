namespace Enma.Application.Authentication;

public static class AuthenticationIdentityErrors
{
    public const string UserIdRequired =
        "Authentication identity user id cannot be empty.";

    public const string EmailMustBeNormalized =
        "Authentication identity email must already be normalized.";

    public const string CredentialUserMismatch =
        "Authentication credential must belong to the same user.";
}
