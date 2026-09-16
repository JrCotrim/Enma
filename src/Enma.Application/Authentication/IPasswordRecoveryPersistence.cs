using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IPasswordRecoveryPersistence
{
    Task<PasswordRecoveryChallengeIssuanceResult> TryIssueOrRotateAsync(
        string normalizedEmail,
        PasswordRecoveryTokenHash tokenHash,
        TimeSpan tokenLifetime,
        TimeSpan resendCooldown,
        CancellationToken cancellationToken = default);

    Task<PasswordRecoveryResetPersistenceResult> TryResetPasswordAsync(
        PasswordRecoveryTokenHash tokenHash,
        string newPassword,
        string newPasswordHash,
        CancellationToken cancellationToken = default);
}
