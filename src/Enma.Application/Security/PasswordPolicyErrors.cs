namespace Enma.Application.Security;

public static class PasswordPolicyErrors
{
    public const string PasswordRequired =
        "Password cannot be null, empty, or whitespace.";
    public const string PasswordTooShort =
        "Password must contain at least 8 characters.";
    public const string PasswordTooLong =
        "Password cannot exceed 128 characters.";
    public const string PasswordMissingUppercase =
        "Password must contain at least one uppercase letter.";
    public const string PasswordMissingLowercase =
        "Password must contain at least one lowercase letter.";
    public const string PasswordMissingNumber =
        "Password must contain at least one number.";
}
