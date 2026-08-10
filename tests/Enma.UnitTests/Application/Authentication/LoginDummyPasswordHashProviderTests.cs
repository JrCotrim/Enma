using Enma.Application.Authentication;
using Enma.Application.Security;

namespace Enma.UnitTests.Application.Authentication;

public sealed class LoginDummyPasswordHashProviderTests
{
    private const string GeneratedHash = "synthetic-generated-dummy-hash";

    [Fact]
    public void Constructor_MultipleHashReads_GeneratesAndRetainsOneHash()
    {
        var passwordHasher = new RecordingPasswordHasher();

        var provider = new LoginDummyPasswordHashProvider(passwordHasher);
        string firstHash = provider.PasswordHash;
        string secondHash = provider.PasswordHash;

        Assert.Equal(1, passwordHasher.HashCallCount);
        Assert.False(string.IsNullOrWhiteSpace(passwordHasher.PasswordToHash));
        Assert.Equal(GeneratedHash, firstHash);
        Assert.Same(firstHash, secondHash);
    }

    private sealed class RecordingPasswordHasher : IPasswordHasher
    {
        public int HashCallCount { get; private set; }

        public string? PasswordToHash { get; private set; }

        public string HashPassword(string password)
        {
            HashCallCount++;
            PasswordToHash = password;
            return GeneratedHash;
        }

        public PasswordVerificationResult VerifyHashedPassword(
            string passwordHash,
            string providedPassword)
        {
            throw new InvalidOperationException(
                "Dummy-hash initialization must not verify a password.");
        }
    }
}
