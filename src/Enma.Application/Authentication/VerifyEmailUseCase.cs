namespace Enma.Application.Authentication;

public sealed class VerifyEmailUseCase
{
    private readonly IEmailVerificationTokenService _tokenService;
    private readonly IEmailVerificationChallengePersistence _challengePersistence;

    public VerifyEmailUseCase(
        IEmailVerificationTokenService tokenService,
        IEmailVerificationChallengePersistence challengePersistence)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(challengePersistence);

        _tokenService = tokenService;
        _challengePersistence = challengePersistence;
    }

    public async Task<VerifyEmailResult> ExecuteAsync(
        string? rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!_tokenService.TryHashToken(rawToken, out var tokenHash)
            || tokenHash is null)
        {
            return VerifyEmailResult.Invalid;
        }

        EmailVerificationChallengeConsumptionPersistenceResult persistenceResult =
            await _challengePersistence.TryConsumeAsync(
                tokenHash,
                cancellationToken);

        return persistenceResult ==
            EmailVerificationChallengeConsumptionPersistenceResult.Succeeded
                ? VerifyEmailResult.Succeeded
                : VerifyEmailResult.Invalid;
    }
}
