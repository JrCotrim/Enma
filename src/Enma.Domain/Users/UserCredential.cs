namespace Enma.Domain.Users;

public sealed class UserCredential
{
    private const int MaximumPasswordHashLength = 512;

    public UserCredential(
        Guid userId,
        string passwordHash,
        DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(UserCredentialErrors.UserIdRequired, nameof(userId));
        }

        string validatedPasswordHash = ValidatePasswordHash(passwordHash);

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                UserCredentialErrors.CreatedAtInvalid);
        }

        UserId = userId;
        PasswordHash = validatedPasswordHash;
        CreatedAt = createdAt;
        PasswordChangedAt = createdAt;
    }

    public Guid UserId { get; private set; }

    public string PasswordHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset PasswordChangedAt { get; private set; }

    public void ChangePasswordHash(
        string passwordHash,
        DateTimeOffset changedAt)
    {
        string validatedPasswordHash = ValidatePasswordHash(passwordHash);

        if (changedAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAt),
                UserCredentialErrors.PasswordChangedAtInvalid);
        }

        if (changedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAt),
                UserCredentialErrors.PasswordChangedBeforeCreation);
        }

        if (changedAt < PasswordChangedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAt),
                UserCredentialErrors.PasswordChangedAtCannotMoveBackward);
        }

        PasswordHash = validatedPasswordHash;
        PasswordChangedAt = changedAt;
    }

    private static string ValidatePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException(
                UserCredentialErrors.PasswordHashRequired,
                nameof(passwordHash));
        }

        if (passwordHash.Length > MaximumPasswordHashLength)
        {
            throw new ArgumentException(
                UserCredentialErrors.PasswordHashTooLong,
                nameof(passwordHash));
        }

        return passwordHash;
    }
}
