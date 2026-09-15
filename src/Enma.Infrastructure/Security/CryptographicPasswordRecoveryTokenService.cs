using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.Infrastructure.Security;

public sealed class CryptographicPasswordRecoveryTokenService
    : IPasswordRecoveryTokenService
{
    private const int EntropyByteLength = 32;
    private const int EncodedTokenLength = 43;

    public string GenerateToken(out PasswordRecoveryTokenHash tokenHash)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(EntropyByteLength);

        try
        {
            string rawToken = Base64Url.EncodeToString(randomBytes);
            tokenHash = HashToken(rawToken);
            return rawToken;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
        }
    }

    public bool TryHashToken(
        string? rawToken,
        out PasswordRecoveryTokenHash? tokenHash)
    {
        tokenHash = null;

        if (rawToken is null ||
            rawToken.Length != EncodedTokenLength ||
            rawToken.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '-' and not '_'))
        {
            return false;
        }

        tokenHash = HashToken(rawToken);
        return true;
    }

    private static PasswordRecoveryTokenHash HashToken(string rawToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(rawToken);
        byte[]? digest = null;

        try
        {
            digest = SHA256.HashData(tokenBytes);
            return new PasswordRecoveryTokenHash(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);

            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }
}
