using Enma.Domain.Users;

namespace Enma.UnitTests.Domain.Users;

public sealed class UserTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidData_GeneratesId()
    {
        User user = CreateUser();

        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public void NormalizeEmail_WithValidInput_ReturnsCanonicalEmail()
    {
        string normalizedEmail = User.NormalizeEmail("  LOGIN@EXAMPLE.TEST  ");

        Assert.Equal("login@example.test", normalizedEmail);
    }

    [Fact]
    public void NormalizeEmail_WithInvalidInput_ThrowsExistingValidationException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            User.NormalizeEmail("invalid-email"));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithValidData_StoresNormalizedName()
    {
        var user = new User("  Maria Silva  ", "maria.silva@example.com", CreatedAt);

        Assert.Equal("Maria Silva", user.Name);
    }

    [Fact]
    public void Constructor_WithValidData_NormalizesEmail()
    {
        var user = new User("Maria Silva", "  MARIA.SILVA@EXAMPLE.COM  ", CreatedAt);

        Assert.Equal("maria.silva@example.com", user.Email);
    }

    [Fact]
    public void Constructor_WithValidData_ActivatesUser()
    {
        User user = CreateUser();

        Assert.True(user.IsActive);
    }

    [Fact]
    public void Constructor_WithValidData_StartsWithUnverifiedEmail()
    {
        User user = CreateUser();

        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public void Constructor_WithValidData_StoresCreatedAt()
    {
        User user = CreateUser();

        Assert.Equal(CreatedAt, user.CreatedAt);
    }

    [Fact]
    public void Constructor_CalledTwice_GeneratesDistinctIds()
    {
        User firstUser = CreateUser();
        User secondUser = CreateUser();

        Assert.NotEqual(firstUser.Id, secondUser.Id);
    }

    [Fact]
    public void Constructor_WithBlankName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new User("   ", "maria.silva@example.com", CreatedAt));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(UserErrors.NameRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNameTooLong_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new User(new string('a', 151), "maria.silva@example.com", CreatedAt));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(UserErrors.NameTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithBlankEmail_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new User("Maria Silva", "   ", CreatedAt));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithEmailTooLong_ThrowsArgumentException()
    {
        string email = $"{new string('a', 250)}@example.com";

        var exception = Assert.Throws<ArgumentException>(() =>
            new User("Maria Silva", email, CreatedAt));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithEmailWithoutAtSign_ThrowsArgumentException()
    {
        AssertInvalidEmail("maria.silva.example.com");
    }

    [Fact]
    public void Constructor_WithEmailContainingMultipleAtSigns_ThrowsArgumentException()
    {
        AssertInvalidEmail("maria@silva@example.com");
    }

    [Fact]
    public void Constructor_WithEmailMissingLocalPart_ThrowsArgumentException()
    {
        AssertInvalidEmail("@example.com");
    }

    [Fact]
    public void Constructor_WithEmailMissingDomainPart_ThrowsArgumentException()
    {
        AssertInvalidEmail("maria.silva@");
    }

    [Fact]
    public void Constructor_WithEmailContainingWhitespace_ThrowsArgumentException()
    {
        AssertInvalidEmail("maria silva@example.com");
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new User("Maria Silva", "maria.silva@example.com", DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(UserErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void Rename_WithValidName_UpdatesName()
    {
        User user = CreateUser();

        user.Rename("  Maria Souza  ");

        Assert.Equal("Maria Souza", user.Name);
    }

    [Fact]
    public void Rename_WithInvalidName_ThrowsArgumentException()
    {
        User user = CreateUser();

        var exception = Assert.Throws<ArgumentException>(() => user.Rename("   "));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(UserErrors.NameRequired, exception.Message);
        Assert.Equal("Maria Silva", user.Name);
    }

    [Fact]
    public void ChangeEmail_WithValidEmail_UpdatesNormalizedEmail()
    {
        User user = CreateUser();

        user.ChangeEmail("  MARIA.SOUZA@EXAMPLE.COM  ");

        Assert.Equal("maria.souza@example.com", user.Email);
    }

    [Fact]
    public void ChangeEmail_WithInvalidEmail_ThrowsArgumentException()
    {
        User user = CreateUser();
        user.VerifyEmail(CreatedAt);

        var exception = Assert.Throws<ArgumentException>(() => user.ChangeEmail("maria.silva.example.com"));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailInvalidFormat, exception.Message);
        Assert.Equal("maria.silva@example.com", user.Email);
        Assert.Equal(CreatedAt, user.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_WithCreatedAtTimestamp_SetsEmailVerifiedAt()
    {
        User user = CreateUser();

        user.VerifyEmail(CreatedAt);

        Assert.Equal(CreatedAt, user.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_WithTimestampAfterCreatedAt_SetsEmailVerifiedAt()
    {
        User user = CreateUser();
        DateTimeOffset verifiedAt = CreatedAt.AddMinutes(1);

        user.VerifyEmail(verifiedAt);

        Assert.Equal(verifiedAt, user.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_WithTimestampBeforeCreatedAt_ThrowsAndPreservesUnverifiedState()
    {
        User user = CreateUser();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            user.VerifyEmail(CreatedAt.AddTicks(-1)));

        Assert.Equal("verifiedAt", exception.ParamName);
        Assert.Contains(UserErrors.EmailVerifiedAtInvalid, exception.Message);
        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_WhenAlreadyVerified_PreservesOriginalTimestamp()
    {
        User user = CreateUser();
        DateTimeOffset firstVerifiedAt = CreatedAt.AddMinutes(1);
        DateTimeOffset laterVerifiedAt = CreatedAt.AddMinutes(2);
        user.VerifyEmail(firstVerifiedAt);

        Exception? repeatedVerificationException = Record.Exception(() =>
            user.VerifyEmail(laterVerifiedAt));

        Assert.Null(repeatedVerificationException);
        Assert.Equal(firstVerifiedAt, user.EmailVerifiedAt);

        var invalidTimestampException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            user.VerifyEmail(CreatedAt.AddTicks(-1)));
        Assert.Equal("verifiedAt", invalidTimestampException.ParamName);
        Assert.Contains(
            UserErrors.EmailVerifiedAtInvalid,
            invalidTimestampException.Message);
        Assert.Equal(firstVerifiedAt, user.EmailVerifiedAt);
    }

    [Fact]
    public void ChangeEmail_WhenNormalizedEmailChanges_ClearsEmailVerification()
    {
        User user = CreateUser();
        user.VerifyEmail(CreatedAt);

        user.ChangeEmail("  MARIA.SOUZA@EXAMPLE.COM  ");

        Assert.Equal("maria.souza@example.com", user.Email);
        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public void ChangeEmail_WithEquivalentNormalizedEmail_PreservesEmailVerification()
    {
        User user = CreateUser();
        user.VerifyEmail(CreatedAt);

        user.ChangeEmail("  MARIA.SILVA@EXAMPLE.COM  ");

        Assert.Equal("maria.silva@example.com", user.Email);
        Assert.Equal(CreatedAt, user.EmailVerifiedAt);
    }

    [Fact]
    public void Deactivate_WhenActive_DeactivatesUser()
    {
        User user = CreateUser();

        user.Deactivate();
        user.Deactivate();

        Assert.False(user.IsActive);
    }

    [Fact]
    public void Activate_WhenInactive_ActivatesUser()
    {
        User user = CreateUser();
        user.Deactivate();

        user.Activate();
        user.Activate();

        Assert.True(user.IsActive);
    }

    private static User CreateUser()
    {
        return new User("Maria Silva", "maria.silva@example.com", CreatedAt);
    }

    private static void AssertInvalidEmail(string email)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new User("Maria Silva", email, CreatedAt));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailInvalidFormat, exception.Message);
    }
}
