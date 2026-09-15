using Enma.Domain.Users;

namespace Enma.Application.Authentication;

public sealed class RequestPasswordRecoveryUseCase
{
    private readonly IPasswordRecoveryTokenService tokenService;
    private readonly IPasswordRecoveryPersistence persistence;
    private readonly IPasswordRecoveryDelivery delivery;

    public RequestPasswordRecoveryUseCase(
        IPasswordRecoveryTokenService tokenService,
        IPasswordRecoveryPersistence persistence,
        IPasswordRecoveryDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);

        this.tokenService = tokenService;
        this.persistence = persistence;
        this.delivery = delivery;
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

        string rawToken = tokenService.GenerateToken(out var tokenHash);
        PasswordRecoveryChallengeIssuanceResult result =
            await persistence.TryIssueOrRotateAsync(
                normalizedEmail,
                tokenHash,
                PasswordRecoveryPolicy.TokenLifetime,
                PasswordRecoveryPolicy.ResendCooldown,
                cancellationToken);

        if (result.Succeeded)
        {
            _ = await delivery.DeliverAsync(
                result.Email ?? throw new InvalidOperationException(
                    "Successful password recovery issuance must include an email."),
                rawToken,
                cancellationToken);
        }
    }
}
