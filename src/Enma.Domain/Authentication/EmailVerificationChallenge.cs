using Enma.Domain.Users;

namespace Enma.Domain.Authentication;

public sealed class EmailVerificationChallenge
{
    public EmailVerificationChallenge(
        Guid userId,
        string emailAtIssue,
        EmailVerificationTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                EmailVerificationChallengeErrors.UserIdRequired,
                nameof(userId));
        }

        string normalizedEmailAtIssue = User.NormalizeEmail(emailAtIssue);

        if (tokenHash is null)
        {
            throw new ArgumentNullException(
                nameof(tokenHash),
                EmailVerificationChallengeErrors.TokenHashRequired);
        }

        ValidateExpiration(createdAt, expiresAt);

        UserId = userId;
        EmailAtIssue = normalizedEmailAtIssue;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid UserId { get; private set; }

    public string EmailAtIssue { get; private set; }

    public EmailVerificationTokenHash TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExpired(DateTimeOffset now)
    {
        return now >= ExpiresAt;
    }

    public void Rotate(
        string emailAtIssue,
        EmailVerificationTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        string normalizedEmailAtIssue = User.NormalizeEmail(emailAtIssue);

        if (tokenHash is null)
        {
            throw new ArgumentNullException(
                nameof(tokenHash),
                EmailVerificationChallengeErrors.TokenHashRequired);
        }

        ValidateExpiration(createdAt, expiresAt);

        if (createdAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                EmailVerificationChallengeErrors.CreatedAtCannotMoveBackward);
        }

        if (TokenHash.Equals(tokenHash))
        {
            throw new ArgumentException(
                EmailVerificationChallengeErrors.TokenHashMustChange,
                nameof(tokenHash));
        }

        EmailAtIssue = normalizedEmailAtIssue;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    private static void ValidateExpiration(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                EmailVerificationChallengeErrors.ExpiresAtInvalid);
        }
    }
}
