namespace Enma.Domain.Users;

public static class UserErrors
{
    public const string NameRequired = "User name cannot be null, empty, or whitespace.";
    public const string NameTooLong = "User name cannot exceed 150 characters.";
    public const string EmailRequired = "User email cannot be null, empty, or whitespace.";
    public const string EmailTooLong = "User email cannot exceed 254 characters.";
    public const string EmailInvalidFormat = "User email format is invalid.";
    public const string CreatedAtInvalid = "User creation date must be a valid value.";
    public const string EmailVerifiedAtInvalid =
        "Email verification date cannot be earlier than user creation date.";
}
