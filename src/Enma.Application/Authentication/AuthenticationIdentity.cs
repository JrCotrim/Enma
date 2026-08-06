using Enma.Domain.Users;

namespace Enma.Application.Authentication;

public sealed class AuthenticationIdentity
{
    public AuthenticationIdentity(
        Guid userId,
        string email,
        bool isActive,
        DateTimeOffset? emailVerifiedAt,
        UserCredential? credential)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                AuthenticationIdentityErrors.UserIdRequired,
                nameof(userId));
        }

        string normalizedEmail = User.NormalizeEmail(email);

        if (!string.Equals(email, normalizedEmail, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                AuthenticationIdentityErrors.EmailMustBeNormalized,
                nameof(email));
        }

        if (credential is not null && credential.UserId != userId)
        {
            throw new ArgumentException(
                AuthenticationIdentityErrors.CredentialUserMismatch,
                nameof(credential));
        }

        UserId = userId;
        Email = email;
        IsActive = isActive;
        EmailVerifiedAt = emailVerifiedAt;
        Credential = credential;
    }

    public Guid UserId { get; }

    public string Email { get; }

    public bool IsActive { get; }

    public DateTimeOffset? EmailVerifiedAt { get; }

    public UserCredential? Credential { get; }
}
