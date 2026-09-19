namespace Enma.Application.Security;

public sealed class DefaultPasswordPolicy : IPasswordPolicy
{
    private const int MinimumPasswordLength = 8;
    private const int MaximumPasswordLength = 128;

    public void Validate(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordRequired,
                nameof(password));
        }

        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordTooShort,
                nameof(password));
        }

        if (password.Length > MaximumPasswordLength)
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordTooLong,
                nameof(password));
        }

        if (!password.Any(char.IsUpper))
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordMissingUppercase,
                nameof(password));
        }

        if (!password.Any(char.IsLower))
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordMissingLowercase,
                nameof(password));
        }

        if (!password.Any(char.IsDigit))
        {
            throw new ArgumentException(
                PasswordPolicyErrors.PasswordMissingNumber,
                nameof(password));
        }
    }
}
