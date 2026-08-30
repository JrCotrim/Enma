using System.Security.Cryptography;
using System.Text;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Security;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class CryptographicOrganizationInvitationTokenServiceTests
{
    private readonly CryptographicOrganizationInvitationTokenService _service =
        new();

    [Fact]
    public void GenerateToken_ReturnsCanonicalBase64UrlTokenAndSha256Hash()
    {
        string token = _service.GenerateToken(out var tokenHash);

        Assert.Equal(43, token.Length);
        Assert.All(token, character =>
            Assert.True(IsBase64UrlCharacter(character)));
        Assert.DoesNotContain('=', token);
        Assert.Equal(32, tokenHash.ToArray().Length);

        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        byte[] digest = SHA256.HashData(tokenBytes);

        try
        {
            Assert.Equal(
                new OrganizationInvitationTokenHash(digest),
                tokenHash);
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
        string firstToken = _service.GenerateToken(out var firstHash);
        string secondToken = _service.GenerateToken(out var secondHash);

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public void TryHashToken_WithGeneratedToken_ReturnsMatchingHash()
    {
        string token = _service.GenerateToken(out var generatedHash);

        bool success = _service.TryHashToken(token, out var parsedHash);

        Assert.True(success);
        Assert.Equal(generatedHash, parsedHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    public void TryHashToken_WithNonCanonicalToken_ReturnsFalse(string? token)
    {
        bool success = _service.TryHashToken(token, out var tokenHash);

        Assert.False(success);
        Assert.Null(tokenHash);
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '-' or '_';
    }
}
