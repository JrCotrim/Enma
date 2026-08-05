using Enma.Application.Security;
using Enma.Domain.Users;
using Enma.Infrastructure.Security;
using Microsoft.Extensions.Options;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Enma.Domain.Users.User>;
using MicrosoftPasswordHasherOptions = Microsoft.AspNetCore.Identity.PasswordHasherOptions;
using MicrosoftPasswordVerificationResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;
using MicrosoftUserPasswordHasher = Microsoft.AspNetCore.Identity.PasswordHasher<Enma.Domain.Users.User>;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class AspNetCorePasswordHasherTests
{
    private const string SyntheticPassword = "Synthetic-Test-Password-Only!";
    private const string DifferentSyntheticPassword =
        "Different-Synthetic-Test-Password-Only!";
    private const string SyntheticHash = "synthetic-hash-not-a-real-credential";

    private static readonly DateTimeOffset CreatedAt = new(
        2025,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    private static readonly User SyntheticUser = new(
        "Synthetic User",
        "synthetic.user@example.test",
        CreatedAt);

    [Fact]
    public void Constructor_WithNullMicrosoftHasher_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new AspNetCorePasswordHasher(null!));

        Assert.Equal("microsoftHasher", exception.ParamName);
    }

    [Fact]
    public void HashPassword_WithNullUser_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.HashPassword(null!, SyntheticPassword));

        Assert.Equal("user", exception.ParamName);
    }

    [Fact]
    public void HashPassword_WithNullPassword_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.HashPassword(SyntheticUser, null!));

        Assert.Equal("password", exception.ParamName);
    }

    [Fact]
    public void VerifyHashedPassword_WithNullUser_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.VerifyHashedPassword(
                null!,
                SyntheticHash,
                SyntheticPassword));

        Assert.Equal("user", exception.ParamName);
    }

    [Fact]
    public void VerifyHashedPassword_WithNullPasswordHash_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.VerifyHashedPassword(
                SyntheticUser,
                null!,
                SyntheticPassword));

        Assert.Equal("passwordHash", exception.ParamName);
    }

    [Fact]
    public void VerifyHashedPassword_WithNullProvidedPassword_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.VerifyHashedPassword(
                SyntheticUser,
                SyntheticHash,
                null!));

        Assert.Equal("providedPassword", exception.ParamName);
    }

    [Fact]
    public void HashPassword_WithSyntheticPassword_ReturnsOpaqueHash()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        string passwordHash = passwordHasher.HashPassword(
            SyntheticUser,
            SyntheticPassword);

        Assert.NotNull(passwordHash);
        Assert.False(string.IsNullOrWhiteSpace(passwordHash));
        Assert.NotEqual(SyntheticPassword, passwordHash);
    }

    [Fact]
    public void HashPassword_CalledTwiceForSameUserAndPassword_ProducesDifferentHashes()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        string firstPasswordHash = passwordHasher.HashPassword(
            SyntheticUser,
            SyntheticPassword);
        string secondPasswordHash = passwordHasher.HashPassword(
            SyntheticUser,
            SyntheticPassword);

        Assert.False(string.IsNullOrWhiteSpace(firstPasswordHash));
        Assert.False(string.IsNullOrWhiteSpace(secondPasswordHash));
        Assert.NotEqual(firstPasswordHash, secondPasswordHash);
    }

    [Fact]
    public void VerifyHashedPassword_WithMatchingPassword_ReturnsSuccess()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();
        string passwordHash = passwordHasher.HashPassword(
            SyntheticUser,
            SyntheticPassword);

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            SyntheticUser,
            passwordHash,
            SyntheticPassword);

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void VerifyHashedPassword_WithDifferentPassword_ReturnsFailed()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();
        string passwordHash = passwordHasher.HashPassword(
            SyntheticUser,
            SyntheticPassword);

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            SyntheticUser,
            passwordHash,
            DifferentSyntheticPassword);

        Assert.Equal(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public void VerifyHashedPassword_WhenMicrosoftRequestsRehash_ReturnsSuccessRehashNeeded()
    {
        var microsoftHasher = new FakeMicrosoftPasswordHasher(
            MicrosoftPasswordVerificationResult.SuccessRehashNeeded);
        var passwordHasher = new AspNetCorePasswordHasher(microsoftHasher);

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            SyntheticUser,
            SyntheticHash,
            SyntheticPassword);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, result);
    }

    [Fact]
    public void VerifyHashedPassword_WithUnsupportedMicrosoftResult_ThrowsInvalidOperationException()
    {
        var microsoftHasher = new FakeMicrosoftPasswordHasher(
            (MicrosoftPasswordVerificationResult)int.MaxValue);
        var passwordHasher = new AspNetCorePasswordHasher(microsoftHasher);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => passwordHasher.VerifyHashedPassword(
                SyntheticUser,
                SyntheticHash,
                SyntheticPassword));

        Assert.Equal(
            "The password hasher returned an unsupported verification result.",
            exception.Message);
        Assert.DoesNotContain(SyntheticPassword, exception.Message);
        Assert.DoesNotContain(SyntheticHash, exception.Message);
        Assert.DoesNotContain(SyntheticUser.Email, exception.Message);
        Assert.DoesNotContain(SyntheticUser.Id.ToString(), exception.Message);
    }

    private static AspNetCorePasswordHasher CreatePasswordHasher()
    {
        var microsoftHasher = new MicrosoftUserPasswordHasher(
            Options.Create(new MicrosoftPasswordHasherOptions()));

        return new AspNetCorePasswordHasher(microsoftHasher);
    }

    private sealed class FakeMicrosoftPasswordHasher(
        MicrosoftPasswordVerificationResult result) : MicrosoftPasswordHasher
    {
        public string HashPassword(User user, string password)
        {
            throw new InvalidOperationException(
                "HashPassword was not expected to be called.");
        }

        public MicrosoftPasswordVerificationResult VerifyHashedPassword(
            User user,
            string hashedPassword,
            string providedPassword)
        {
            return result;
        }
    }
}
