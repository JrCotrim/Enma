using System.Security.Cryptography;
using System.Text;
using Enma.Domain.Authentication;
using Enma.Infrastructure.Security;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class CryptographicEmailVerificationTokenServiceTests
{
    private readonly CryptographicEmailVerificationTokenService _service =
        new();

    [Fact]
    public void GenerateToken_ReturnsCanonicalTokenAndMatchingHash()
    {
        string token = _service.GenerateToken(out var tokenHash);

        Assert.Equal(43, token.Length);
        Assert.All(token, character =>
            Assert.True(IsBase64UrlCharacter(character)));
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain(token, char.IsWhiteSpace);
        Assert.NotNull(tokenHash);
        Assert.Equal(32, tokenHash.ToArray().Length);

        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        byte[] digest = SHA256.HashData(tokenBytes);

        try
        {
            var expectedHash = new EmailVerificationTokenHash(digest);
            Assert.Equal(expectedHash, tokenHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [Fact]
    public void GenerateToken_CalledTwice_ReturnsDifferentTokensAndHashes()
    {
        string firstToken = _service.GenerateToken(out var firstTokenHash);
        string secondToken = _service.GenerateToken(out var secondTokenHash);

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(firstTokenHash, secondTokenHash);
    }

    [Fact]
    public void TryHashToken_WithGeneratedToken_ReturnsMatchingHash()
    {
        string token = _service.GenerateToken(out var generatedTokenHash);

        bool result = _service.TryHashToken(token, out var parsedTokenHash);

        Assert.True(result);
        Assert.NotNull(parsedTokenHash);
        Assert.Equal(generatedTokenHash, parsedTokenHash);
    }

    [Fact]
    public void TryHashToken_WithNullOrWrongLength_ReturnsFalse()
    {
        string?[] invalidTokens =
        [
            null,
            string.Empty,
            new string('A', 42),
            new string('A', 44)
        ];

        foreach (string? invalidToken in invalidTokens)
        {
            bool result = _service.TryHashToken(
                invalidToken,
                out var tokenHash);

            Assert.False(result);
            Assert.Null(tokenHash);
        }
    }

    [Fact]
    public void TryHashToken_WithInvalidCharacters_ReturnsFalse()
    {
        string validPrefix = new('A', 42);
        string[] invalidTokens =
        [
            validPrefix + '=',
            validPrefix + '+',
            validPrefix + '/',
            validPrefix + ' ',
            validPrefix + '\t',
            validPrefix + '\n',
            validPrefix + '\u0001',
            validPrefix + '\uFF21'
        ];

        foreach (string invalidToken in invalidTokens)
        {
            bool result = _service.TryHashToken(
                invalidToken,
                out var tokenHash);

            Assert.False(result);
            Assert.Null(tokenHash);
        }
    }

    [Fact]
    public void TryHashToken_WithSameValidTokenTwice_ReturnsValueEqualHashes()
    {
        string validToken = new('A', 43);

        bool firstResult = _service.TryHashToken(
            validToken,
            out var firstTokenHash);
        bool secondResult = _service.TryHashToken(
            validToken,
            out var secondTokenHash);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.NotNull(firstTokenHash);
        Assert.NotNull(secondTokenHash);
        Assert.Equal(firstTokenHash, secondTokenHash);
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '-' or '_';
    }
}
