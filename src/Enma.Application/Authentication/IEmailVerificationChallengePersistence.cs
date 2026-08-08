using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IEmailVerificationChallengePersistence
{
    Task<EmailVerificationChallengeIssuancePersistenceResult>
        TryIssueOrRotateAsync(
            Guid userId,
            EmailVerificationTokenHash tokenHash,
            TimeSpan tokenLifetime,
            TimeSpan resendCooldown,
            CancellationToken cancellationToken = default);

    Task<EmailVerificationChallengeConsumptionPersistenceResult>
        TryConsumeAsync(
            EmailVerificationTokenHash tokenHash,
            CancellationToken cancellationToken = default);
}
