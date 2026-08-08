using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IEmailVerificationTokenService
{
    string GenerateToken(
        out EmailVerificationTokenHash tokenHash);

    bool TryHashToken(
        string? rawToken,
        out EmailVerificationTokenHash? tokenHash);
}
