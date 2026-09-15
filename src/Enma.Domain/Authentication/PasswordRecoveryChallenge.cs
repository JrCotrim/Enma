using Enma.Domain.Users;

namespace Enma.Domain.Authentication;

public sealed class PasswordRecoveryChallenge
{
    public PasswordRecoveryChallenge(
        Guid userId,
        string emailAtIssue,
        PasswordRecoveryTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                PasswordRecoveryChallengeErrors.UserIdRequired,
                nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(tokenHash);
        ValidateExpiration(createdAt, expiresAt);

        UserId = userId;
        EmailAtIssue = User.NormalizeEmail(emailAtIssue);
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid UserId { get; private set; }

    public string EmailAtIssue { get; private set; }

    public PasswordRecoveryTokenHash TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void Rotate(
        string emailAtIssue,
        PasswordRecoveryTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ValidateExpiration(createdAt, expiresAt);

        if (createdAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                PasswordRecoveryChallengeErrors.CreatedAtCannotMoveBackward);
        }

        if (TokenHash.Equals(tokenHash))
        {
            throw new ArgumentException(
                PasswordRecoveryChallengeErrors.TokenHashMustChange,
                nameof(tokenHash));
        }

        EmailAtIssue = User.NormalizeEmail(emailAtIssue);
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    private static void ValidateExpiration(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (createdAt == DateTimeOffset.MinValue || expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                PasswordRecoveryChallengeErrors.ExpiresAtInvalid);
        }
    }
}
