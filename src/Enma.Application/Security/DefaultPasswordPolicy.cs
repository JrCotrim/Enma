namespace Enma.Application.Security;

public sealed class DefaultPasswordPolicy : IPasswordPolicy
{
    private const int MinimumPasswordLength = 15;
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
    }
}
