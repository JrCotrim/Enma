using System.Security.Cryptography;
using System.Text;
using Enma.Domain.Authentication;
using Enma.Infrastructure.Security;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class CryptographicPasswordRecoveryTokenServiceTests
{
    private readonly CryptographicPasswordRecoveryTokenService service = new();

    [Fact]
    public void GeneratedTokensAreCanonicalUniqueAndHashOnlyToSha256()
    {
        string first = service.GenerateToken(out var firstHash);
        string second = service.GenerateToken(out var secondHash);

        Assert.Equal(43, first.Length);
        Assert.All(first, character => Assert.True(character is
            >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'));
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstHash, secondHash);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(first));
        Assert.Equal(new PasswordRecoveryTokenHash(digest), firstHash);
        Assert.True(service.TryHashToken(first, out var parsedHash));
        Assert.Equal(firstHash, parsedHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void MalformedTokensAreRejected(string? rawToken)
    {
        Assert.False(service.TryHashToken(rawToken, out var tokenHash));
        Assert.Null(tokenHash);
    }
}
