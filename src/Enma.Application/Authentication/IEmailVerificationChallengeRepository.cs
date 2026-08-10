using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IEmailVerificationChallengeRepository
{
    Task AddAsync(
        EmailVerificationChallenge challenge,
        CancellationToken cancellationToken = default);
}
