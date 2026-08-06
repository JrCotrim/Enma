namespace Enma.Application.Security;

public static class PasswordPolicyErrors
{
    public const string PasswordRequired =
        "Password cannot be null, empty, or whitespace.";
    public const string PasswordTooShort =
        "Password must contain at least 15 characters.";
    public const string PasswordTooLong =
        "Password cannot exceed 128 characters.";
}
