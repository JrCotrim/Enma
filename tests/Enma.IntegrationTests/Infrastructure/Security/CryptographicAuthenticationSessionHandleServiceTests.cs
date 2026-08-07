using System.Security.Cryptography;
using System.Text;
using Enma.Domain.Authentication;
using Enma.Infrastructure.Security;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class CryptographicAuthenticationSessionHandleServiceTests
{
    private readonly CryptographicAuthenticationSessionHandleService _service =
        new();

    [Fact]
    public void GenerateHandle_ReturnsCanonicalHandleAndMatchingSecretHash()
    {
        string handle = _service.GenerateHandle(out var secretHash);

        Assert.Equal(43, handle.Length);
        Assert.All(handle, character =>
            Assert.True(IsBase64UrlCharacter(character)));
        Assert.DoesNotContain('=', handle);
        Assert.DoesNotContain('+', handle);
        Assert.DoesNotContain('/', handle);
        Assert.DoesNotContain(handle, char.IsWhiteSpace);
        Assert.NotNull(secretHash);
        Assert.Equal(32, secretHash.ToArray().Length);

        byte[] handleBytes = Encoding.UTF8.GetBytes(handle);
        byte[] digest = SHA256.HashData(handleBytes);

        try
        {
            var expectedHash = new AuthenticationSessionSecretHash(digest);
            Assert.Equal(expectedHash, secretHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(handleBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [Fact]
    public void GenerateHandle_CalledTwice_ReturnsDifferentHandlesAndHashes()
    {
        string firstHandle = _service.GenerateHandle(out var firstSecretHash);
        string secondHandle = _service.GenerateHandle(out var secondSecretHash);

        Assert.NotEqual(firstHandle, secondHandle);
        Assert.NotEqual(firstSecretHash, secondSecretHash);
    }

    [Fact]
    public void TryHashHandle_WithGeneratedHandle_ReturnsMatchingHash()
    {
        string handle = _service.GenerateHandle(out var generatedSecretHash);

        bool result = _service.TryHashHandle(handle, out var parsedSecretHash);

        Assert.True(result);
        Assert.NotNull(parsedSecretHash);
        Assert.Equal(generatedSecretHash, parsedSecretHash);
    }

    [Fact]
    public void TryHashHandle_WithNullOrWrongLength_ReturnsFalse()
    {
        string?[] invalidHandles =
        [
            null,
            string.Empty,
            new string('A', 42),
            new string('A', 44)
        ];

        foreach (string? invalidHandle in invalidHandles)
        {
            bool result = _service.TryHashHandle(
                invalidHandle,
                out var secretHash);

            Assert.False(result);
            Assert.Null(secretHash);
        }
    }

    [Fact]
    public void TryHashHandle_WithInvalidCharacters_ReturnsFalse()
    {
        string validPrefix = new('A', 42);
        string[] invalidHandles =
        [
            validPrefix + '=',
            validPrefix + '+',
            validPrefix + '/',
            validPrefix + ' ',
            validPrefix + '\n',
            validPrefix + '\uFF21'
        ];

        foreach (string invalidHandle in invalidHandles)
        {
            bool result = _service.TryHashHandle(
                invalidHandle,
                out var secretHash);

            Assert.False(result);
            Assert.Null(secretHash);
        }
    }

    [Fact]
    public void TryHashHandle_WithSameValidHandleTwice_ReturnsValueEqualHashes()
    {
        string validHandle = new('A', 43);

        bool firstResult = _service.TryHashHandle(
            validHandle,
            out var firstSecretHash);
        bool secondResult = _service.TryHashHandle(
            validHandle,
            out var secondSecretHash);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.NotNull(firstSecretHash);
        Assert.NotNull(secondSecretHash);
        Assert.Equal(firstSecretHash, secondSecretHash);
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '-' or '_';
    }
}
