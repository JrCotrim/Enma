using Enma.Domain.Users;

namespace Enma.UnitTests.Domain.Users;

public sealed class UserCredentialTests
{
    private static readonly Guid UserId = Guid.Parse("5f0ba6c8-c52b-4a57-89ca-79ca382adf4c");
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ChangedAt = new(2026, 8, 6, 15, 45, 0, TimeSpan.Zero);
    private const string InitialHash = "$opaque$AbC123+/=_-.:~SyntheticValue";
    private const string ChangedHash = "$opaque$XyZ789+/=_-.:~ChangedSyntheticValue";
    private const string SecondChangedHash = "$opaque$MnO456+/=_-.:~SecondChangedSyntheticValue";

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
    public void Constructor_WithValidData_StartsAtCredentialVersionOne()
    {
        UserCredential credential = CreateCredential();

        Assert.Equal(1, credential.CredentialVersion);
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
    public void ChangePasswordHash_WithValidData_IncrementsCredentialVersion()
    {
        UserCredential credential = CreateCredential();

        credential.ChangePasswordHash(ChangedHash, ChangedAt);

        Assert.Equal(ChangedHash, credential.PasswordHash);
        Assert.Equal(ChangedAt, credential.PasswordChangedAt);
        Assert.Equal(2, credential.CredentialVersion);
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
        Assert.Equal(1, credential.CredentialVersion);
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
        Assert.Equal(1, credential.CredentialVersion);
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
        Assert.Equal(1, credential.CredentialVersion);
    }

    [Fact]
    public void ChangePasswordHash_WithTimestampBeforeCurrentPasswordChangedAt_ThrowsAndPreservesState()
    {
        UserCredential credential = CreateCredential();
        credential.ChangePasswordHash(ChangedHash, ChangedAt);
        string storedPasswordHash = credential.PasswordHash;
        DateTimeOffset storedPasswordChangedAt = credential.PasswordChangedAt;
        long storedCredentialVersion = credential.CredentialVersion;
        DateTimeOffset earlierChangedAt = CreatedAt.AddHours(1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            credential.ChangePasswordHash(SecondChangedHash, earlierChangedAt));

        Assert.Equal("changedAt", exception.ParamName);
        Assert.Contains(
            UserCredentialErrors.PasswordChangedAtCannotMoveBackward,
            exception.Message);
        Assert.Equal(storedPasswordHash, credential.PasswordHash);
        Assert.Equal(storedPasswordChangedAt, credential.PasswordChangedAt);
        Assert.Equal(storedCredentialVersion, credential.CredentialVersion);
        Assert.Equal(CreatedAt, credential.CreatedAt);
        Assert.Equal(UserId, credential.UserId);
    }

    [Fact]
    public void ChangePasswordHash_WithTimestampEqualToCurrentPasswordChangedAt_Succeeds()
    {
        UserCredential credential = CreateCredential();
        credential.ChangePasswordHash(ChangedHash, ChangedAt);

        credential.ChangePasswordHash(SecondChangedHash, ChangedAt);

        Assert.Equal(SecondChangedHash, credential.PasswordHash);
        Assert.Equal(ChangedAt, credential.PasswordChangedAt);
        Assert.Equal(CreatedAt, credential.CreatedAt);
        Assert.Equal(UserId, credential.UserId);
    }

    [Fact]
    public void ChangePasswordHash_WithEqualTimestamp_IncrementsCredentialVersion()
    {
        UserCredential credential = CreateCredential();
        DateTimeOffset originalPasswordChangedAt = credential.PasswordChangedAt;

        credential.ChangePasswordHash(ChangedHash, originalPasswordChangedAt);

        Assert.Equal(ChangedHash, credential.PasswordHash);
        Assert.Equal(originalPasswordChangedAt, credential.PasswordChangedAt);
        Assert.Equal(2, credential.CredentialVersion);
    }

    [Fact]
    public void ChangePasswordHash_CalledTwice_IncrementsCredentialVersionForEachChange()
    {
        UserCredential credential = CreateCredential();

        credential.ChangePasswordHash(ChangedHash, ChangedAt);

        Assert.Equal(2, credential.CredentialVersion);

        DateTimeOffset secondChangedAt = ChangedAt.AddHours(1);
        credential.ChangePasswordHash(SecondChangedHash, secondChangedAt);

        Assert.Equal(3, credential.CredentialVersion);
        Assert.Equal(SecondChangedHash, credential.PasswordHash);
        Assert.Equal(secondChangedAt, credential.PasswordChangedAt);
    }

    [Fact]
    public void UpgradePasswordHash_WithValidHash_ChangesOnlyPasswordHash()
    {
        UserCredential credential = CreateCredential();
        DateTimeOffset originalPasswordChangedAt = credential.PasswordChangedAt;
        long originalCredentialVersion = credential.CredentialVersion;

        credential.UpgradePasswordHash(ChangedHash);

        Assert.Equal(ChangedHash, credential.PasswordHash);
        Assert.Equal(originalPasswordChangedAt, credential.PasswordChangedAt);
        Assert.Equal(originalCredentialVersion, credential.CredentialVersion);
    }

    [Fact]
    public void UpgradePasswordHash_WithInvalidHash_ThrowsAndPreservesState()
    {
        UserCredential credential = CreateCredential();
        string originalPasswordHash = credential.PasswordHash;
        DateTimeOffset originalPasswordChangedAt = credential.PasswordChangedAt;
        long originalCredentialVersion = credential.CredentialVersion;

        var exception = Assert.Throws<ArgumentException>(() =>
            credential.UpgradePasswordHash("   "));

        Assert.Equal("passwordHash", exception.ParamName);
        Assert.Contains(UserCredentialErrors.PasswordHashRequired, exception.Message);
        Assert.Equal(originalPasswordHash, credential.PasswordHash);
        Assert.Equal(originalPasswordChangedAt, credential.PasswordChangedAt);
        Assert.Equal(originalCredentialVersion, credential.CredentialVersion);
    }

    private static UserCredential CreateCredential()
    {
        return new UserCredential(UserId, InitialHash, CreatedAt);
    }
}
