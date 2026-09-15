using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IPasswordRecoveryTokenService
{
    string GenerateToken(out PasswordRecoveryTokenHash tokenHash);

    bool TryHashToken(
        string? rawToken,
        out PasswordRecoveryTokenHash? tokenHash);
}
