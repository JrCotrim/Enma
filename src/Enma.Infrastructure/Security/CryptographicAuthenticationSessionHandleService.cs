using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.Infrastructure.Security;

public sealed class CryptographicAuthenticationSessionHandleService
    : IAuthenticationSessionHandleService
{
    private const int EntropyByteLength = 32;
    private const int EncodedHandleLength = 43;

    public string GenerateHandle(
        out AuthenticationSessionSecretHash secretHash)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(EntropyByteLength);

        try
        {
            string rawHandle = Base64Url.EncodeToString(randomBytes);

            if (rawHandle.Length != EncodedHandleLength)
            {
                throw new CryptographicException(
                    "Generated authentication session handle has an invalid length.");
            }

            secretHash = HashHandle(rawHandle);
            return rawHandle;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
        }
    }

    public bool TryHashHandle(
        string? rawHandle,
        out AuthenticationSessionSecretHash? secretHash)
    {
        secretHash = null;

        if (rawHandle is null ||
            rawHandle.Length != EncodedHandleLength ||
            !ContainsOnlyBase64UrlCharacters(rawHandle))
        {
            return false;
        }

        secretHash = HashHandle(rawHandle);
        return true;
    }

    private static AuthenticationSessionSecretHash HashHandle(string rawHandle)
    {
        byte[] handleBytes = Encoding.UTF8.GetBytes(rawHandle);
        byte[]? digest = null;

        try
        {
            digest = SHA256.HashData(handleBytes);
            return new AuthenticationSessionSecretHash(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(handleBytes);

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
