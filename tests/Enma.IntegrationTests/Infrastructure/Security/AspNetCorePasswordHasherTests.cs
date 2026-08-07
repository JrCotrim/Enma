using Enma.Application.Security;
using Enma.Infrastructure.Security;
using Microsoft.Extensions.Options;
using LegacyMicrosoftUserPasswordHasher =
    Microsoft.AspNetCore.Identity.PasswordHasher<Enma.Domain.Users.User>;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;
using MicrosoftPasswordHasherOptions = Microsoft.AspNetCore.Identity.PasswordHasherOptions;
using MicrosoftPasswordVerificationResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;
using MicrosoftUserPasswordHasher = Microsoft.AspNetCore.Identity.PasswordHasher<object>;

namespace Enma.IntegrationTests.Infrastructure.Security;

public sealed class AspNetCorePasswordHasherTests
{
    private const string SyntheticPassword = "Synthetic-Test-Password-Only!";
    private const string DifferentSyntheticPassword =
        "Different-Synthetic-Test-Password-Only!";
    private const string SyntheticHash = "synthetic-hash-not-a-real-credential";

    [Fact]
    public void Constructor_WithNullMicrosoftHasher_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new AspNetCorePasswordHasher(null!));

        Assert.Equal("microsoftHasher", exception.ParamName);
    }

    [Fact]
    public void IPasswordHasher_WithPublicMethods_HasUserIndependentSignatures()
    {
        var hashMethod = typeof(IPasswordHasher).GetMethod(
            nameof(IPasswordHasher.HashPassword),
            [typeof(string)]);
        var verifyMethod = typeof(IPasswordHasher).GetMethod(
            nameof(IPasswordHasher.VerifyHashedPassword),
            [typeof(string), typeof(string)]);

        Assert.NotNull(hashMethod);
        Assert.Equal(typeof(string), hashMethod.ReturnType);
        Assert.Equal(
            new[] { typeof(string) },
            hashMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(verifyMethod);
        Assert.Equal(typeof(PasswordVerificationResult), verifyMethod.ReturnType);
        Assert.Equal(
            new[] { typeof(string), typeof(string) },
            verifyMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(IPasswordHasher)
                .GetMethods()
                .SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(Enma.Domain.Users.User));
    }

    [Fact]
    public void HashPassword_WithNullPassword_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.HashPassword(null!));

        Assert.Equal("password", exception.ParamName);
    }

    [Fact]
    public void HashAndVerify_WithoutCallerUser_UseSameNonNullProviderMarker()
    {
        var microsoftHasher = new FakeMicrosoftPasswordHasher(
            MicrosoftPasswordVerificationResult.Success);
        var passwordHasher = new AspNetCorePasswordHasher(microsoftHasher);

        string passwordHash = passwordHasher.HashPassword(SyntheticPassword);
        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            passwordHash,
            SyntheticPassword);

        Assert.Equal(SyntheticHash, passwordHash);
        Assert.Equal(PasswordVerificationResult.Success, result);
        Assert.NotNull(microsoftHasher.HashProviderUser);
        Assert.Same(
            microsoftHasher.HashProviderUser,
            microsoftHasher.VerificationProviderUser);
    }

    [Fact]
    public void VerifyHashedPassword_WithNullPasswordHash_ThrowsArgumentNullException()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => passwordHasher.VerifyHashedPassword(
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
                SyntheticHash,
                null!));

        Assert.Equal("providedPassword", exception.ParamName);
    }

    [Fact]
    public void HashPassword_WithSyntheticPassword_ReturnsOpaqueHash()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        string passwordHash = passwordHasher.HashPassword(SyntheticPassword);

        Assert.NotNull(passwordHash);
        Assert.False(string.IsNullOrWhiteSpace(passwordHash));
        Assert.NotEqual(SyntheticPassword, passwordHash);
    }

    [Fact]
    public void HashPassword_CalledTwiceForSamePassword_ProducesDifferentHashes()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        string firstPasswordHash = passwordHasher.HashPassword(SyntheticPassword);
        string secondPasswordHash = passwordHasher.HashPassword(SyntheticPassword);

        Assert.False(string.IsNullOrWhiteSpace(firstPasswordHash));
        Assert.False(string.IsNullOrWhiteSpace(secondPasswordHash));
        Assert.NotEqual(firstPasswordHash, secondPasswordHash);
    }

    [Fact]
    public void VerifyHashedPassword_WithMatchingPassword_ReturnsSuccess()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();
        string passwordHash = passwordHasher.HashPassword(SyntheticPassword);

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            passwordHash,
            SyntheticPassword);

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void VerifyHashedPassword_WithHashProducedByPreviousUserGenericProvider_ReturnsSuccess()
    {
        var legacyProvider = new LegacyMicrosoftUserPasswordHasher(
            Options.Create(new MicrosoftPasswordHasherOptions()));
        var legacyProviderUser = new Enma.Domain.Users.User(
            "Synthetic Legacy Provider User",
            "synthetic.legacy.provider@example.test",
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        string legacyHash = legacyProvider.HashPassword(
            legacyProviderUser,
            SyntheticPassword);
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
            legacyHash,
            SyntheticPassword);

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void VerifyHashedPassword_WithDifferentPassword_ReturnsFailed()
    {
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();
        string passwordHash = passwordHasher.HashPassword(SyntheticPassword);

        PasswordVerificationResult result = passwordHasher.VerifyHashedPassword(
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
                SyntheticHash,
                SyntheticPassword));

        Assert.Equal(
            "The password hasher returned an unsupported verification result.",
            exception.Message);
        Assert.DoesNotContain(SyntheticPassword, exception.Message);
        Assert.DoesNotContain(SyntheticHash, exception.Message);
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
        public object? HashProviderUser { get; private set; }

        public object? VerificationProviderUser { get; private set; }

        public string HashPassword(object user, string password)
        {
            HashProviderUser = user;

            return SyntheticHash;
        }

        public MicrosoftPasswordVerificationResult VerifyHashedPassword(
            object user,
            string hashedPassword,
            string providedPassword)
        {
            VerificationProviderUser = user;

            return result;
        }
    }
}
