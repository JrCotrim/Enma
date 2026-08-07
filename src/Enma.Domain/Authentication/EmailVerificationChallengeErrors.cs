namespace Enma.Domain.Authentication;

public static class EmailVerificationChallengeErrors
{
    public const string UserIdRequired = "User id cannot be empty.";
    public const string TokenHashRequired =
        "Email verification token hash is required.";
    public const string TokenHashLengthInvalid =
        "Email verification token hash must contain exactly 32 bytes.";
    public const string ExpiresAtInvalid =
        "Email verification expiration must be after creation.";
    public const string CreatedAtCannotMoveBackward =
        "Email verification creation timestamp cannot move backward.";
    public const string TokenHashMustChange =
        "Email verification rotation must use a new token hash.";
}
