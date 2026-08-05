namespace Enma.Domain.Users;

public static class UserCredentialErrors
{
    public const string UserIdRequired = "User id cannot be empty.";
    public const string PasswordHashRequired = "Password hash cannot be null, empty, or whitespace.";
    public const string PasswordHashTooLong = "Password hash cannot exceed 512 characters.";
    public const string CreatedAtInvalid = "User credential creation date must be a valid value.";
    public const string PasswordChangedAtInvalid = "Password change date must be a valid value.";
    public const string PasswordChangedBeforeCreation =
        "Password change date cannot be earlier than the credential creation date.";
}
