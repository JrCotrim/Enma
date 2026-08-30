using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;

namespace Enma.Infrastructure.Security;

public sealed class CryptographicOrganizationInvitationTokenService
    : IOrganizationInvitationTokenService
{
    private const int EntropyByteLength = 32;
    private const int EncodedTokenLength = 43;

    public string GenerateToken(out OrganizationInvitationTokenHash tokenHash)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(EntropyByteLength);

        try
        {
            string rawToken = Base64Url.EncodeToString(randomBytes);

            if (rawToken.Length != EncodedTokenLength)
            {
                throw new CryptographicException(
                    "Generated organization invitation token has an invalid length.");
            }

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
        out OrganizationInvitationTokenHash? tokenHash)
    {
        tokenHash = null;

        if (rawToken is null ||
            rawToken.Length != EncodedTokenLength ||
            !ContainsOnlyBase64UrlCharacters(rawToken))
        {
            return false;
        }

        tokenHash = HashToken(rawToken);
        return true;
    }

    private static OrganizationInvitationTokenHash HashToken(string rawToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(rawToken);
        byte[]? digest = null;

        try
        {
            digest = SHA256.HashData(tokenBytes);
            return new OrganizationInvitationTokenHash(digest);
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

    private static bool ContainsOnlyBase64UrlCharacters(string value)
    {
        foreach (char character in value)
        {
            bool isValid = character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '-' or '_';

            if (!isValid)
            {
                return false;
            }
        }

        return true;
    }
}
