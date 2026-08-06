using Enma.Application.Authentication;
using Enma.Domain.Users;

namespace Enma.UnitTests.Application.Authentication;

public sealed class AuthenticationIdentityTests
{
    private const string SyntheticPasswordHash =
        "synthetic-opaque-hash-authentication-identity-001";

    private static readonly Guid UserId = Guid.Parse(
        "11111111-2222-3333-4444-555555555555");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        6,
        10,
        20,
        30,
        TimeSpan.Zero);

    private static readonly DateTimeOffset EmailVerifiedAt = CreatedAt.AddMinutes(5);

    [Fact]
    public void Constructor_WithValidDataWithoutCredential_CreatesIdentity()
    {
        var identity = new AuthenticationIdentity(
            UserId,
            "identity@example.test",
            false,
            EmailVerifiedAt,
            null);

        Assert.Equal(UserId, identity.UserId);
        Assert.Equal("identity@example.test", identity.Email);
        Assert.False(identity.IsActive);
        Assert.Equal(EmailVerifiedAt, identity.EmailVerifiedAt);
        Assert.Null(identity.Credential);
    }

    [Fact]
    public void Constructor_WithMatchingCredential_CreatesIdentity()
    {
        var credential = new UserCredential(
            UserId,
            SyntheticPasswordHash,
            CreatedAt);

        var identity = new AuthenticationIdentity(
            UserId,
            "identity@example.test",
            true,
            null,
            credential);

        Assert.Same(credential, identity.Credential);
    }

    [Fact]
    public void Constructor_WithEmptyUserId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthenticationIdentity(
                Guid.Empty,
                "identity@example.test",
                true,
                null,
                null));

        Assert.Equal("userId", exception.ParamName);
        Assert.Contains(AuthenticationIdentityErrors.UserIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNonNormalizedEmail_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthenticationIdentity(
                UserId,
                "  IDENTITY@EXAMPLE.TEST  ",
                true,
                null,
                null));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(
            AuthenticationIdentityErrors.EmailMustBeNormalized,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithCredentialForAnotherUser_ThrowsArgumentException()
    {
        var credential = new UserCredential(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            SyntheticPasswordHash,
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthenticationIdentity(
                UserId,
                "identity@example.test",
                true,
                null,
                credential));

        Assert.Equal("credential", exception.ParamName);
        Assert.Contains(
            AuthenticationIdentityErrors.CredentialUserMismatch,
            exception.Message);
    }
}
