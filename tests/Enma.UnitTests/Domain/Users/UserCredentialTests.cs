using Enma.Domain.Users;

namespace Enma.UnitTests.Domain.Users;

public sealed class UserCredentialTests
{
    private static readonly Guid UserId = Guid.Parse("5f0ba6c8-c52b-4a57-89ca-79ca382adf4c");
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ChangedAt = new(2026, 8, 6, 15, 45, 0, TimeSpan.Zero);
    private const string InitialHash = "$opaque$AbC123+/=_-.:~SyntheticValue";
    private const string ChangedHash = "$opaque$XyZ789+/=_-.:~ChangedSyntheticValue";

    [Fact]
    public void Constructor_WithValidData_StoresUserId()
    {
        UserCredential credential = CreateCredential();

        Assert.Equal(UserId, credential.UserId);
    }

    [Fact]
    public void Constructor_WithValidData_PreservesPasswordHashExactly()
    {
        var credential = new UserCredential(UserId, InitialHash, CreatedAt);

        Assert.Equal(InitialHash, credential.PasswordHash);
    }

    [Fact]
    public void Constructor_WithValidData_StoresCreatedAt()
    {
        UserCredential credential = CreateCredential();

        Assert.Equal(CreatedAt, credential.CreatedAt);
    }

    [Fact]
    public void Constructor_WithValidData_SetsPasswordChangedAtToCreatedAt()
    {
        UserCredential credential = CreateCredential();

        Assert.Equal(CreatedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void Constructor_WithEmptyUserId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new UserCredential(Guid.Empty, InitialHash, CreatedAt));

        Assert.Equal("userId", exception.ParamName);
        Assert.Contains(UserCredentialErrors.UserIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithBlankPasswordHash_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new UserCredential(UserId, "   ", CreatedAt));

        Assert.Equal("passwordHash", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordHashRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithPasswordHashTooLong_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new UserCredential(UserId, new string('a', 513), CreatedAt));

        Assert.Equal("passwordHash", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordHashTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UserCredential(UserId, InitialHash, DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(UserCredentialErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void ChangePasswordHash_WithValidData_UpdatesHash()
    {
        UserCredential credential = CreateCredential();

        credential.ChangePasswordHash(ChangedHash, ChangedAt);

        Assert.Equal(ChangedHash, credential.PasswordHash);
    }

    [Fact]
    public void ChangePasswordHash_WithValidData_UpdatesPasswordChangedAt()
    {
        UserCredential credential = CreateCredential();

        credential.ChangePasswordHash(ChangedHash, ChangedAt);

        Assert.Equal(ChangedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void ChangePasswordHash_WithTimestampEqualToCreatedAt_Succeeds()
    {
        UserCredential credential = CreateCredential();

        credential.ChangePasswordHash(ChangedHash, CreatedAt);

        Assert.Equal(ChangedHash, credential.PasswordHash);
        Assert.Equal(CreatedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void ChangePasswordHash_WithInvalidHash_PreservesExistingState()
    {
        UserCredential credential = CreateCredential();

        var exception = Assert.Throws<ArgumentException>(() =>
            credential.ChangePasswordHash("   ", ChangedAt));

        Assert.Equal("passwordHash", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordHashRequired, exception.Message);
        Assert.Equal(InitialHash, credential.PasswordHash);
        Assert.Equal(CreatedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void ChangePasswordHash_WithMinimumTimestamp_ThrowsAndPreservesExistingState()
    {
        UserCredential credential = CreateCredential();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            credential.ChangePasswordHash(ChangedHash, DateTimeOffset.MinValue));

        Assert.Equal("changedAt", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordChangedAtInvalid, exception.Message);
        Assert.Equal(InitialHash, credential.PasswordHash);
        Assert.Equal(CreatedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void ChangePasswordHash_WithTimestampBeforeCreation_ThrowsAndPreservesExistingState()
    {
        UserCredential credential = CreateCredential();
        DateTimeOffset changedAt = CreatedAt.AddTicks(-1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            credential.ChangePasswordHash(ChangedHash, changedAt));

        Assert.Equal("changedAt", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordChangedBeforeCreation, exception.Message);
        Assert.Equal(InitialHash, credential.PasswordHash);
        Assert.Equal(CreatedAt, credential.PasswordChangedAt);
    }

    private static UserCredential CreateCredential()
    {
        return new UserCredential(UserId, InitialHash, CreatedAt);
    }
}
