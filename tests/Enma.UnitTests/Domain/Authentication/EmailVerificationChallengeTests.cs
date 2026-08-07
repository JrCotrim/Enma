using Enma.Domain.Authentication;
using Enma.Domain.Users;

namespace Enma.UnitTests.Domain.Authentication;

public sealed class EmailVerificationChallengeTests
{
    private static readonly Guid UserId =
        Guid.Parse("72973c48-027f-44ea-acf7-7d8f1d64f654");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        7,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddHours(1);

    [Fact]
    public void Constructor_WithValidData_InitializesChallenge()
    {
        const string emailAtIssue = "  OWNER@EXAMPLE.TEST  ";
        EmailVerificationTokenHash tokenHash = CreateTokenHash(1);

        var challenge = new EmailVerificationChallenge(
            UserId,
            emailAtIssue,
            tokenHash,
            CreatedAt,
            ExpiresAt);

        Assert.Equal(UserId, challenge.UserId);
        Assert.Equal(User.NormalizeEmail(emailAtIssue), challenge.EmailAtIssue);
        Assert.Same(tokenHash, challenge.TokenHash);
        Assert.Equal(CreatedAt, challenge.CreatedAt);
        Assert.Equal(ExpiresAt, challenge.ExpiresAt);
    }

    [Fact]
    public void Constructor_WithEmptyUserId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateChallenge(userId: Guid.Empty));

        Assert.Equal("userId", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.UserIdRequired,
            exception.Message);
    }

    [Theory]
    [InlineData("   ", UserErrors.EmailRequired)]
    [InlineData("invalid-email", UserErrors.EmailInvalidFormat)]
    public void Constructor_WithInvalidEmail_ThrowsExistingValidationException(
        string emailAtIssue,
        string expectedError)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateChallenge(emailAtIssue: emailAtIssue));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(expectedError, exception.Message);
    }

    [Fact]
    public void Constructor_WithNullTokenHash_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new EmailVerificationChallenge(
                UserId,
                "owner@example.test",
                null!,
                CreatedAt,
                ExpiresAt));

        Assert.Equal("tokenHash", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashRequired,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithExpirationEqualToCreation_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateChallenge(expiresAt: CreatedAt));

        Assert.Equal("expiresAt", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.ExpiresAtInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithExpirationBeforeCreation_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateChallenge(expiresAt: CreatedAt.AddTicks(-1)));

        Assert.Equal("expiresAt", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.ExpiresAtInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsExpired_AtExpirationBoundary_ReturnsExpectedResult(
        int tickOffset,
        bool expectedResult)
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        DateTimeOffset now = ExpiresAt.AddTicks(tickOffset);

        bool result = challenge.IsExpired(now);

        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void Rotate_WithValidData_ReplacesChallengeState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        const string emailAtIssue = "  NEW.OWNER@EXAMPLE.TEST  ";
        EmailVerificationTokenHash tokenHash = CreateTokenHash(2);
        DateTimeOffset createdAt = CreatedAt.AddMinutes(10);
        DateTimeOffset expiresAt = createdAt.AddHours(2);

        challenge.Rotate(emailAtIssue, tokenHash, createdAt, expiresAt);

        Assert.Equal(UserId, challenge.UserId);
        Assert.Equal(User.NormalizeEmail(emailAtIssue), challenge.EmailAtIssue);
        Assert.Same(tokenHash, challenge.TokenHash);
        Assert.Equal(createdAt, challenge.CreatedAt);
        Assert.Equal(expiresAt, challenge.ExpiresAt);
    }

    [Fact]
    public void Rotate_WithCreationEqualToCurrent_ReplacesChallengeState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        EmailVerificationTokenHash tokenHash = CreateTokenHash(2);
        DateTimeOffset expiresAt = ExpiresAt.AddMinutes(10);

        challenge.Rotate(
            "new.owner@example.test",
            tokenHash,
            CreatedAt,
            expiresAt);

        Assert.Same(tokenHash, challenge.TokenHash);
        Assert.Equal(CreatedAt, challenge.CreatedAt);
        Assert.Equal(expiresAt, challenge.ExpiresAt);
    }

    [Fact]
    public void Rotate_WithNullTokenHash_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            challenge.Rotate(
                "new.owner@example.test",
                null!,
                CreatedAt.AddMinutes(10),
                ExpiresAt.AddMinutes(10)));

        Assert.Equal("tokenHash", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashRequired,
            exception.Message);
        AssertState(challenge, originalState);
    }

    [Fact]
    public void Rotate_WithSameTokenHash_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);

        var exception = Assert.Throws<ArgumentException>(() =>
            challenge.Rotate(
                "new.owner@example.test",
                new EmailVerificationTokenHash(challenge.TokenHash.ToArray()),
                CreatedAt.AddMinutes(10),
                ExpiresAt.AddMinutes(10)));

        Assert.Equal("tokenHash", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashMustChange,
            exception.Message);
        AssertState(challenge, originalState);
    }

    [Fact]
    public void Rotate_WithRegressedCreation_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            challenge.Rotate(
                "new.owner@example.test",
                CreateTokenHash(2),
                CreatedAt.AddTicks(-1),
                ExpiresAt));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.CreatedAtCannotMoveBackward,
            exception.Message);
        AssertState(challenge, originalState);
    }

    [Fact]
    public void Rotate_WithExpirationEqualToCreation_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);
        DateTimeOffset createdAt = CreatedAt.AddMinutes(10);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            challenge.Rotate(
                "new.owner@example.test",
                CreateTokenHash(2),
                createdAt,
                createdAt));

        Assert.Equal("expiresAt", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.ExpiresAtInvalid,
            exception.Message);
        AssertState(challenge, originalState);
    }

    [Fact]
    public void Rotate_WithExpirationBeforeCreation_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);
        DateTimeOffset createdAt = CreatedAt.AddMinutes(10);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            challenge.Rotate(
                "new.owner@example.test",
                CreateTokenHash(2),
                createdAt,
                createdAt.AddTicks(-1)));

        Assert.Equal("expiresAt", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.ExpiresAtInvalid,
            exception.Message);
        AssertState(challenge, originalState);
    }

    [Fact]
    public void Rotate_WithInvalidEmail_ThrowsAndPreservesState()
    {
        EmailVerificationChallenge challenge = CreateChallenge();
        ChallengeState originalState = Snapshot(challenge);

        var exception = Assert.Throws<ArgumentException>(() =>
            challenge.Rotate(
                "invalid-email",
                CreateTokenHash(2),
                CreatedAt.AddMinutes(10),
                ExpiresAt.AddMinutes(10)));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(UserErrors.EmailInvalidFormat, exception.Message);
        AssertState(challenge, originalState);
    }

    private static EmailVerificationChallenge CreateChallenge(
        Guid? userId = null,
        string emailAtIssue = "owner@example.test",
        EmailVerificationTokenHash? tokenHash = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        return new EmailVerificationChallenge(
            userId ?? UserId,
            emailAtIssue,
            tokenHash ?? CreateTokenHash(1),
            createdAt ?? CreatedAt,
            expiresAt ?? ExpiresAt);
    }

    private static EmailVerificationTokenHash CreateTokenHash(byte seed)
    {
        byte[] value = Enumerable.Range(seed, 32)
            .Select(item => (byte)item)
            .ToArray();

        return new EmailVerificationTokenHash(value);
    }

    private static ChallengeState Snapshot(EmailVerificationChallenge challenge)
    {
        return new ChallengeState(
            challenge.EmailAtIssue,
            challenge.TokenHash,
            challenge.CreatedAt,
            challenge.ExpiresAt);
    }

    private static void AssertState(
        EmailVerificationChallenge challenge,
        ChallengeState expected)
    {
        Assert.Equal(expected.EmailAtIssue, challenge.EmailAtIssue);
        Assert.Same(expected.TokenHash, challenge.TokenHash);
        Assert.Equal(expected.CreatedAt, challenge.CreatedAt);
        Assert.Equal(expected.ExpiresAt, challenge.ExpiresAt);
    }

    private sealed record ChallengeState(
        string EmailAtIssue,
        EmailVerificationTokenHash TokenHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
