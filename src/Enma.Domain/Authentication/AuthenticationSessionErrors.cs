namespace Enma.Domain.Authentication;

public static class AuthenticationSessionErrors
{
    public const string UserIdRequired = "User id cannot be empty.";
    public const string SecretHashRequired = "Session secret hash is required.";
    public const string SecretHashLengthInvalid =
        "Session secret hash must contain exactly 32 bytes.";
    public const string CredentialVersionAtIssueInvalid =
        "Credential version at issue must be positive.";
    public const string CreatedAtInvalid =
        "Session creation date must be a valid value.";
    public const string IdleExpiresAtInvalid =
        "Session idle expiration is invalid.";
    public const string AbsoluteExpiresAtInvalid =
        "Session absolute expiration is invalid.";
    public const string LastSeenAtCannotMoveBackward =
        "Session activity timestamp cannot move backward.";
    public const string IdleExpiresAtCannotMoveBackward =
        "Session idle expiration cannot move backward.";
    public const string RevokedAtInvalid =
        "Session revocation date cannot be earlier than its creation date.";
    public const string SelectedOrganizationIdInvalid =
        "Selected organization id cannot be empty.";
    public const string SessionRevoked =
        "A revoked session cannot be modified.";
    public const string ConcurrencyVersionInvalid =
        "Session concurrency version must be positive.";
}
