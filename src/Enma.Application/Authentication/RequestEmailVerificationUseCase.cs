using Enma.Domain.Users;

namespace Enma.Application.Authentication;

public sealed class RequestEmailVerificationUseCase
{
    private readonly IEmailVerificationUserLookup _userLookup;
    private readonly IEmailVerificationTokenService _tokenService;
    private readonly IEmailVerificationChallengePersistence _challengePersistence;
    private readonly IEmailVerificationDelivery _delivery;

    public RequestEmailVerificationUseCase(
        IEmailVerificationUserLookup userLookup,
        IEmailVerificationTokenService tokenService,
        IEmailVerificationChallengePersistence challengePersistence,
        IEmailVerificationDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(userLookup);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(challengePersistence);
        ArgumentNullException.ThrowIfNull(delivery);

        _userLookup = userLookup;
        _tokenService = tokenService;
        _challengePersistence = challengePersistence;
        _delivery = delivery;
    }

    public async Task ExecuteAsync(
        string? email,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail;

        try
        {
            normalizedEmail = User.NormalizeEmail(email ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return;
        }

        Guid? userId = await _userLookup.FindUserIdByEmailAsync(
            normalizedEmail,
            cancellationToken);

        if (userId is null)
        {
            return;
        }

        string rawToken = _tokenService.GenerateToken(out var tokenHash);
        EmailVerificationChallengeIssuancePersistenceResult issuanceResult =
            await _challengePersistence.TryIssueOrRotateAsync(
                userId.Value,
                tokenHash,
                EmailVerificationPolicy.TokenLifetime,
                EmailVerificationPolicy.ResendCooldown,
                cancellationToken);

        if (!issuanceResult.Succeeded)
        {
            return;
        }

        string emailAtIssue = issuanceResult.EmailAtIssue
            ?? throw new InvalidOperationException(
                "Successful email verification issuance must include an email.");

        _ = await _delivery.DeliverAsync(
            emailAtIssue,
            rawToken,
            cancellationToken);
    }
}
