namespace Enma.Domain.Authentication;

public static class PasswordRecoveryChallengeErrors
{
    public const string UserIdRequired = "The user identifier is required.";
    public const string EmailAtIssueRequired = "The email at issue is required.";
    public const string TokenHashRequired = "The token hash is required.";
    public const string TokenHashLengthInvalid = "The token hash must contain 32 bytes.";
    public const string ExpiresAtInvalid = "The expiration must be later than creation.";
    public const string CreatedAtCannotMoveBackward = "The creation timestamp cannot move backward.";
    public const string TokenHashMustChange = "The token hash must change when rotating a challenge.";
}
