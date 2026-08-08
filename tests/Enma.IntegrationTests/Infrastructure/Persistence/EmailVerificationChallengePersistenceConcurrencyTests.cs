using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailVerificationChallengePersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly Guid FirstUserId = Guid.Parse(
        "00000000-0000-0000-0000-000000000301");
    private static readonly Guid SecondUserId = Guid.Parse(
        "00000000-0000-0000-0000-000000000302");
    private static readonly DateTimeOffset UserCreatedAt = new(
        2026,
        8,
        8,
        9,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset OperationTime =
        UserCreatedAt.AddHours(2);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(30);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithInvalidArguments_ThrowsProgrammingErrors()
    {
        EmailVerificationChallengePersistence persistence = CreatePersistence();
        EmailVerificationTokenHash tokenHash = CreateTokenHash(3);

        await Assert.ThrowsAsync<ArgumentException>(
            () => persistence.TryIssueOrRotateAsync(
                Guid.Empty,
                tokenHash,
                TokenLifetime,
                ResendCooldown));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => persistence.TryIssueOrRotateAsync(
                FirstUserId,
                null!,
                TokenLifetime,
                ResendCooldown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => persistence.TryIssueOrRotateAsync(
                FirstUserId,
                tokenHash,
                TimeSpan.Zero,
                ResendCooldown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => persistence.TryIssueOrRotateAsync(
                FirstUserId,
                tokenHash,
                TimeSpan.FromTicks(-1),
                ResendCooldown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => persistence.TryIssueOrRotateAsync(
                FirstUserId,
                tokenHash,
                TokenLifetime,
                TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public async Task TryConsumeAsync_WithNullHash_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreatePersistence().TryConsumeAsync(null!));
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithEligibleUser_CreatesCurrentEmailChallenge()
    {
        await SeedUserAsync(FirstUserId, "current-issue@example.test");
        EmailVerificationTokenHash tokenHash = CreateTokenHash(11);
        var timeProvider = new MutableTimeProvider(OperationTime);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence(timeProvider).TryIssueOrRotateAsync(
                FirstUserId,
                tokenHash,
                TokenLifetime,
                ResendCooldown);

        Assert.True(result.Succeeded);
        Assert.Equal("current-issue@example.test", result.EmailAtIssue);

        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("current-issue@example.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(tokenHash, persistedChallenge.TokenHash);
        Assert.Equal(OperationTime, persistedChallenge.CreatedAt);
        Assert.Equal(OperationTime.Add(TokenLifetime), persistedChallenge.ExpiresAt);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithInactiveUser_RejectsWithoutChallenge()
    {
        await SeedUserAsync(
            FirstUserId,
            "inactive-issue@example.test",
            isActive: false);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                CreateTokenHash(21),
                TokenLifetime,
                ResendCooldown);

        Assert.False(result.Succeeded);
        Assert.Null(result.EmailAtIssue);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithVerifiedUser_RejectsWithoutRotation()
    {
        EmailVerificationTokenHash originalHash = CreateTokenHash(31);
        DateTimeOffset originalCreatedAt = OperationTime.AddMinutes(-40);
        await SeedUserAsync(
            FirstUserId,
            "verified-issue@example.test",
            isVerified: true,
            challenge: CreateChallenge(
                FirstUserId,
                "verified-issue@example.test",
                originalHash,
                originalCreatedAt));
        ChallengeState originalState = await LoadChallengeStateAsync(FirstUserId);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                CreateTokenHash(32),
                TokenLifetime,
                ResendCooldown);

        Assert.False(result.Succeeded);
        Assert.Null(result.EmailAtIssue);
        Assert.Equal(originalState, await LoadChallengeStateAsync(FirstUserId));
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_InsideSameEmailCooldown_RejectsUnchangedChallenge()
    {
        EmailVerificationTokenHash originalHash = CreateTokenHash(41);
        DateTimeOffset originalCreatedAt = OperationTime.AddMinutes(-29);
        await SeedUserAsync(
            FirstUserId,
            "cooldown@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "cooldown@example.test",
                originalHash,
                originalCreatedAt));
        ChallengeState originalState = await LoadChallengeStateAsync(FirstUserId);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                CreateTokenHash(42),
                TokenLifetime,
                ResendCooldown);

        Assert.False(result.Succeeded);
        Assert.Null(result.EmailAtIssue);
        Assert.Equal(originalState, await LoadChallengeStateAsync(FirstUserId));
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_AtCooldownBoundary_PermitsRotation()
    {
        await SeedUserAsync(
            FirstUserId,
            "cooldown-boundary@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "cooldown-boundary@example.test",
                CreateTokenHash(51),
                OperationTime.Subtract(ResendCooldown)));
        EmailVerificationTokenHash rotatedHash = CreateTokenHash(52);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                rotatedHash,
                TokenLifetime,
                ResendCooldown);

        Assert.True(result.Succeeded);
        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal(rotatedHash, persistedChallenge.TokenHash);
        Assert.Equal(OperationTime, persistedChallenge.CreatedAt);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithOldEmailChallenge_BypassesOldAddressCooldown()
    {
        await SeedUserAsync(
            FirstUserId,
            "new-current@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "old-address@example.test",
                CreateTokenHash(61),
                OperationTime.AddMinutes(-1)));
        EmailVerificationTokenHash rotatedHash = CreateTokenHash(62);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                rotatedHash,
                TokenLifetime,
                ResendCooldown);

        Assert.True(result.Succeeded);
        Assert.Equal("new-current@example.test", result.EmailAtIssue);
        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("new-current@example.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(rotatedHash, persistedChallenge.TokenHash);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithAllowedExistingChallenge_UpdatesAllRotationState()
    {
        await SeedUserAsync(
            FirstUserId,
            "rotation@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "rotation@example.test",
                CreateTokenHash(71),
                OperationTime.AddHours(-1)));
        EmailVerificationTokenHash rotatedHash = CreateTokenHash(72);
        TimeSpan rotatedLifetime = TimeSpan.FromHours(3);

        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                rotatedHash,
                rotatedLifetime,
                ResendCooldown);

        Assert.True(result.Succeeded);
        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("rotation@example.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(rotatedHash, persistedChallenge.TokenHash);
        Assert.Equal(OperationTime, persistedChallenge.CreatedAt);
        Assert.Equal(OperationTime.Add(rotatedLifetime), persistedChallenge.ExpiresAt);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithCollidingHash_RollsBackEntireRotation()
    {
        EmailVerificationChallenge firstChallenge = CreateChallenge(
            FirstUserId,
            "collision-one@example.test",
            CreateTokenHash(81),
            OperationTime.AddHours(-1));
        EmailVerificationChallenge secondChallenge = CreateChallenge(
            SecondUserId,
            "collision-two@example.test",
            CreateTokenHash(82),
            OperationTime.AddHours(-1));
        await SeedUserAsync(
            FirstUserId,
            "collision-one@example.test",
            challenge: firstChallenge);
        await SeedUserAsync(
            SecondUserId,
            "collision-two@example.test",
            challenge: secondChallenge);
        ChallengeState originalState = await LoadChallengeStateAsync(FirstUserId);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                new EmailVerificationTokenHash(secondChallenge.TokenHash.ToArray()),
                TokenLifetime,
                ResendCooldown));

        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "ux_email_verification_challenges_token_hash",
            postgresException.ConstraintName);
        Assert.Equal(originalState, await LoadChallengeStateAsync(FirstUserId));
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithTwoConcurrentAttempts_SerializesToOneSuccess()
    {
        await SeedUserAsync(FirstUserId, "two-issues@example.test");
        var timeProvider = new MutableTimeProvider(OperationTime);
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(blockerContext, FirstUserId, timeout.Token);

        EmailVerificationTokenHash firstHash = CreateTokenHash(91);
        EmailVerificationTokenHash secondHash = CreateTokenHash(92);
        Task<EmailVerificationChallengeIssuancePersistenceResult> firstTask =
            CreatePersistence(timeProvider).TryIssueOrRotateAsync(
                FirstUserId,
                firstHash,
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        Task<EmailVerificationChallengeIssuancePersistenceResult> secondTask =
            CreatePersistence(timeProvider).TryIssueOrRotateAsync(
                FirstUserId,
                secondHash,
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            2,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeIssuancePersistenceResult[] results =
            await Task.WhenAll(firstTask, secondTask).WaitAsync(timeout.Token);

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded);
        EmailVerificationChallenge finalChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.True(
            finalChallenge.TokenHash.Equals(firstHash) ||
            finalChallenge.TokenHash.Equals(secondHash));
        Assert.Equal(
            results[0].Succeeded ? firstHash : secondHash,
            finalChallenge.TokenHash);
        Assert.Equal(
            finalChallenge.EmailAtIssue,
            Assert.Single(results, result => result.Succeeded).EmailAtIssue);
    }

    [Fact]
    public async Task TryConsumeAsync_WithValidCurrentToken_VerifiesAndDeletesAtomically()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(101);
        await SeedUserAsync(
            FirstUserId,
            "valid-consume@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "valid-consume@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Succeeded,
            result);
        User persistedUser = await LoadUserAsync(FirstUserId);
        Assert.Equal(OperationTime, persistedUser.EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WithUnknownHash_RejectsWithoutUnrelatedMutation()
    {
        EmailVerificationTokenHash currentHash = CreateTokenHash(111);
        await SeedUserAsync(
            FirstUserId,
            "unknown-hash@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "unknown-hash@example.test",
                currentHash,
                OperationTime.AddMinutes(-5)));
        ChallengeState originalState = await LoadChallengeStateAsync(FirstUserId);

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(CreateTokenHash(112));

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        Assert.Equal(originalState, await LoadChallengeStateAsync(FirstUserId));
    }

    [Fact]
    public async Task TryConsumeAsync_WithExpiredChallenge_RejectsAndDeletesMatchedChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(121);
        await SeedUserAsync(
            FirstUserId,
            "expired@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "expired@example.test",
                tokenHash,
                OperationTime.AddHours(-3),
                OperationTime.AddMinutes(-1)));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_AtExactExpirationBoundary_RejectsAndDeletesMatchedChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(131);
        await SeedUserAsync(
            FirstUserId,
            "expiration-boundary@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "expiration-boundary@example.test",
                tokenHash,
                OperationTime.AddHours(-2),
                OperationTime));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WithCurrentEmailMismatch_RejectsAndDeletesMatchedChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(141);
        await SeedUserAsync(
            FirstUserId,
            "new-address@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "old-address@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        User persistedUser = await LoadUserAsync(FirstUserId);
        Assert.Equal("new-address@example.test", persistedUser.Email);
        Assert.Null(persistedUser.EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WithInactiveUser_RejectsAndDeletesMatchedChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(151);
        await SeedUserAsync(
            FirstUserId,
            "inactive-consume@example.test",
            isActive: false,
            challenge: CreateChallenge(
                FirstUserId,
                "inactive-consume@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WithAlreadyVerifiedUser_PreservesVerificationAndDeletesChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(161);
        DateTimeOffset originalVerificationTime = UserCreatedAt.AddMinutes(10);
        await SeedUserAsync(
            FirstUserId,
            "verified-consume@example.test",
            isVerified: true,
            challenge: CreateChallenge(
                FirstUserId,
                "verified-consume@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));

        EmailVerificationChallengeConsumptionPersistenceResult result =
            await CreatePersistence().TryConsumeAsync(tokenHash);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Equal(
            originalVerificationTime,
            (await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenRotationCommitsFirst_RejectsOldHashWithoutDeletingNewChallenge()
    {
        EmailVerificationTokenHash oldHash = CreateTokenHash(171);
        EmailVerificationTokenHash newHash = CreateTokenHash(172);
        await SeedUserAsync(
            FirstUserId,
            "rotation-race@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "rotation-race@example.test",
                oldHash,
                OperationTime.AddHours(-1)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockChallengeTableForWritesAsync(blockerContext, timeout.Token);

        Task<EmailVerificationChallengeIssuancePersistenceResult> rotationTask =
            CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                newHash,
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        await WaitForBlockedCommandAsync(
            "UPDATE",
            "email_verification_challenges",
            1,
            timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence().TryConsumeAsync(oldHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeIssuancePersistenceResult rotationResult =
            await rotationTask.WaitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult consumeResult =
            await consumeTask.WaitAsync(timeout.Token);

        Assert.True(rotationResult.Succeeded);
        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            consumeResult);
        EmailVerificationChallenge finalChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal(newHash, finalChallenge.TokenHash);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
    }

    [Fact]
    public async Task TryConsumeAsync_WithTwoConcurrentAttempts_SerializesToSingleUse()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(181);
        await SeedUserAsync(
            FirstUserId,
            "two-consumes@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "two-consumes@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(blockerContext, FirstUserId, timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> firstTask =
            CreatePersistence().TryConsumeAsync(tokenHash, timeout.Token);
        Task<EmailVerificationChallengeConsumptionPersistenceResult> secondTask =
            CreatePersistence().TryConsumeAsync(tokenHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            2,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult[] results =
            await Task.WhenAll(firstTask, secondTask).WaitAsync(timeout.Token);

        Assert.Single(
            results,
            result => result ==
                EmailVerificationChallengeConsumptionPersistenceResult.Succeeded);
        Assert.Single(
            results,
            result => result ==
                EmailVerificationChallengeConsumptionPersistenceResult.Rejected);
        Assert.Equal(OperationTime, (await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenClockAdvancesDuringUserLockWait_UsesPostLockTime()
    {
        DateTimeOffset expiresAt = OperationTime.AddMinutes(1);
        EmailVerificationTokenHash tokenHash = CreateTokenHash(191);
        await SeedUserAsync(
            FirstUserId,
            "post-lock-clock@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "post-lock-clock@example.test",
                tokenHash,
                OperationTime.AddHours(-1),
                expiresAt));
        var timeProvider = new MutableTimeProvider(OperationTime);
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(blockerContext, FirstUserId, timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence(timeProvider).TryConsumeAsync(tokenHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        timeProvider.SetUtcNow(expiresAt);
        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult result =
            await consumeTask.WaitAsync(timeout.Token);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        Assert.Null((await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WhenConsumeHoldsUserLock_RejectsAfterConsumeCommits()
    {
        EmailVerificationTokenHash currentHash = CreateTokenHash(201);
        await SeedUserAsync(
            FirstUserId,
            "consume-first@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "consume-first@example.test",
                currentHash,
                OperationTime.AddHours(-1)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockChallengeTableForWritesAsync(blockerContext, timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence().TryConsumeAsync(currentHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "DELETE",
            "email_verification_challenges",
            1,
            timeout.Token);

        Task<EmailVerificationChallengeIssuancePersistenceResult> issuanceTask =
            CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                CreateTokenHash(202),
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult consumeResult =
            await consumeTask.WaitAsync(timeout.Token);
        EmailVerificationChallengeIssuancePersistenceResult issuanceResult =
            await issuanceTask.WaitAsync(timeout.Token);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Succeeded,
            consumeResult);
        Assert.False(issuanceResult.Succeeded);
        Assert.Equal(OperationTime, (await LoadUserAsync(FirstUserId)).EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenEmailChangeCommitsFirst_RejectsOldEmailToken()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(211);
        await SeedUserAsync(
            FirstUserId,
            "email-before-old@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "email-before-old@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext emailChangeContext = fixture.CreateDbContext();
        await using IDbContextTransaction emailChangeTransaction =
            await emailChangeContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        User lockedUser = await LockUserAsync(
            emailChangeContext,
            FirstUserId,
            timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence().TryConsumeAsync(tokenHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        lockedUser.ChangeEmail("email-before-new@example.test");
        await emailChangeContext.SaveChangesAsync(timeout.Token);
        await emailChangeTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult result =
            await consumeTask.WaitAsync(timeout.Token);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        User persistedUser = await LoadUserAsync(FirstUserId);
        Assert.Equal("email-before-new@example.test", persistedUser.Email);
        Assert.Null(persistedUser.EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenConsumeLocksFirst_EmailChangeLeavesNewEmailUnverified()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(221);
        await SeedUserAsync(
            FirstUserId,
            "consume-email-old@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "consume-email-old@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockChallengeTableForWritesAsync(blockerContext, timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence().TryConsumeAsync(tokenHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "DELETE",
            "email_verification_challenges",
            1,
            timeout.Token);

        Task emailChangeTask = ChangeEmailWithUserLockAsync(
            FirstUserId,
            "consume-email-new@example.test",
            timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult consumeResult =
            await consumeTask.WaitAsync(timeout.Token);
        await emailChangeTask.WaitAsync(timeout.Token);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Succeeded,
            consumeResult);
        User persistedUser = await LoadUserAsync(FirstUserId);
        Assert.Equal("consume-email-new@example.test", persistedUser.Email);
        Assert.Null(persistedUser.EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WhenEmailChangeCommitsFirst_BindsNewCurrentEmail()
    {
        await SeedUserAsync(
            FirstUserId,
            "change-before-issue-old@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "change-before-issue-old@example.test",
                CreateTokenHash(231),
                OperationTime.AddMinutes(-1)));
        EmailVerificationTokenHash newHash = CreateTokenHash(232);
        using var timeout = CreateTimeout();

        await using EnmaDbContext emailChangeContext = fixture.CreateDbContext();
        await using IDbContextTransaction emailChangeTransaction =
            await emailChangeContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        User lockedUser = await LockUserAsync(
            emailChangeContext,
            FirstUserId,
            timeout.Token);

        Task<EmailVerificationChallengeIssuancePersistenceResult> issuanceTask =
            CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                newHash,
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        lockedUser.ChangeEmail("change-before-issue-new@example.test");
        await emailChangeContext.SaveChangesAsync(timeout.Token);
        await emailChangeTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeIssuancePersistenceResult result =
            await issuanceTask.WaitAsync(timeout.Token);

        Assert.True(result.Succeeded);
        Assert.Equal("change-before-issue-new@example.test", result.EmailAtIssue);
        EmailVerificationChallenge challenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("change-before-issue-new@example.test", challenge.EmailAtIssue);
        Assert.Equal(newHash, challenge.TokenHash);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WhenIssuanceLocksFirst_EmailChangeMakesChallengeStale()
    {
        EmailVerificationTokenHash newHash = CreateTokenHash(242);
        await SeedUserAsync(
            FirstUserId,
            "issue-before-change-old@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "issue-before-change-old@example.test",
                CreateTokenHash(241),
                OperationTime.AddHours(-1)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockChallengeTableForWritesAsync(blockerContext, timeout.Token);

        Task<EmailVerificationChallengeIssuancePersistenceResult> issuanceTask =
            CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                newHash,
                TokenLifetime,
                ResendCooldown,
                timeout.Token);
        await WaitForBlockedCommandAsync(
            "UPDATE",
            "email_verification_challenges",
            1,
            timeout.Token);

        Task emailChangeTask = ChangeEmailWithUserLockAsync(
            FirstUserId,
            "issue-before-change-new@example.test",
            timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        await blockerTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeIssuancePersistenceResult issuanceResult =
            await issuanceTask.WaitAsync(timeout.Token);
        await emailChangeTask.WaitAsync(timeout.Token);

        Assert.True(issuanceResult.Succeeded);
        User persistedUser = await LoadUserAsync(FirstUserId);
        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("issue-before-change-new@example.test", persistedUser.Email);
        Assert.Null(persistedUser.EmailVerifiedAt);
        Assert.Equal(
            "issue-before-change-old@example.test",
            persistedChallenge.EmailAtIssue);
        Assert.Equal(newHash, persistedChallenge.TokenHash);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenDeactivationCommitsDuringLockWait_RejectsAndCleansChallenge()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(251);
        await SeedUserAsync(
            FirstUserId,
            "deactivation-race@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "deactivation-race@example.test",
                tokenHash,
                OperationTime.AddMinutes(-5)));
        using var timeout = CreateTimeout();

        await using EnmaDbContext deactivationContext = fixture.CreateDbContext();
        await using IDbContextTransaction deactivationTransaction =
            await deactivationContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        User lockedUser = await LockUserAsync(
            deactivationContext,
            FirstUserId,
            timeout.Token);

        Task<EmailVerificationChallengeConsumptionPersistenceResult> consumeTask =
            CreatePersistence().TryConsumeAsync(tokenHash, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            1,
            timeout.Token);

        lockedUser.Deactivate();
        await deactivationContext.SaveChangesAsync(timeout.Token);
        await deactivationTransaction.CommitAsync(timeout.Token);
        EmailVerificationChallengeConsumptionPersistenceResult result =
            await consumeTask.WaitAsync(timeout.Token);

        Assert.Equal(
            EmailVerificationChallengeConsumptionPersistenceResult.Rejected,
            result);
        User persistedUser = await LoadUserAsync(FirstUserId);
        Assert.False(persistedUser.IsActive);
        Assert.Null(persistedUser.EmailVerifiedAt);
        await AssertChallengeAbsentAsync(FirstUserId);
    }

    [Fact]
    public async Task TryIssueOrRotateAsync_WithStaleCallerTracking_UsesFreshCurrentDatabaseState()
    {
        await SeedUserAsync(
            FirstUserId,
            "stale-caller-old@example.test",
            challenge: CreateChallenge(
                FirstUserId,
                "stale-caller-old@example.test",
                CreateTokenHash(1),
                OperationTime.AddMinutes(-1)));
        await using EnmaDbContext callerContext = fixture.CreateDbContext();
        User staleUser = await callerContext.Users.SingleAsync(
            user => user.Id == FirstUserId);
        EmailVerificationChallenge staleChallenge = await callerContext
            .EmailVerificationChallenges
            .SingleAsync(challenge => challenge.UserId == FirstUserId);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            User currentUser = await updateContext.Users.SingleAsync(
                user => user.Id == FirstUserId);
            currentUser.ChangeEmail("stale-caller-new@example.test");
            await updateContext.SaveChangesAsync();
        }

        EmailVerificationTokenHash newHash = CreateTokenHash(2);
        EmailVerificationChallengeIssuancePersistenceResult result =
            await CreatePersistence().TryIssueOrRotateAsync(
                FirstUserId,
                newHash,
                TokenLifetime,
                ResendCooldown);

        Assert.True(result.Succeeded);
        Assert.Equal("stale-caller-new@example.test", result.EmailAtIssue);
        Assert.Equal("stale-caller-old@example.test", staleUser.Email);
        Assert.Equal("stale-caller-old@example.test", staleChallenge.EmailAtIssue);
        Assert.Single(callerContext.ChangeTracker.Entries<User>());
        Assert.Single(
            callerContext.ChangeTracker.Entries<EmailVerificationChallenge>());
        EmailVerificationChallenge persistedChallenge =
            await LoadChallengeAsync(FirstUserId);
        Assert.Equal("stale-caller-new@example.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(newHash, persistedChallenge.TokenHash);
    }

    private EmailVerificationChallengePersistence CreatePersistence(
        TimeProvider? timeProvider = null)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new EmailVerificationChallengePersistence(
            options,
            timeProvider ?? new MutableTimeProvider(OperationTime));
    }

    private async Task SeedUserAsync(
        Guid userId,
        string email,
        bool isActive = true,
        bool isVerified = false,
        EmailVerificationChallenge? challenge = null)
    {
        var user = new User("Email Verification Concurrency User", email, UserCreatedAt);

        if (!isActive)
        {
            user.Deactivate();
        }

        if (isVerified)
        {
            user.VerifyEmail(UserCreatedAt.AddMinutes(10));
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.Entry(user).Property(candidate => candidate.Id).CurrentValue = userId;

        if (challenge is not null)
        {
            dbContext.EmailVerificationChallenges.Add(challenge);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<User> LoadUserAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId);
    }

    private async Task<EmailVerificationChallenge> LoadChallengeAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.EmailVerificationChallenges
            .AsNoTracking()
            .SingleAsync(challenge => challenge.UserId == userId);
    }

    private async Task<ChallengeState> LoadChallengeStateAsync(Guid userId)
    {
        EmailVerificationChallenge challenge = await LoadChallengeAsync(userId);
        return new ChallengeState(
            challenge.EmailAtIssue,
            challenge.TokenHash,
            challenge.CreatedAt,
            challenge.ExpiresAt);
    }

    private async Task AssertChallengeAbsentAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.EmailVerificationChallenges
            .AsNoTracking()
            .AnyAsync(challenge => challenge.UserId == userId));
    }

    private async Task ChangeEmailWithUserLockAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        User user = await LockUserAsync(dbContext, userId, cancellationToken);
        user.ChangeEmail(email);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task WaitForBlockedCommandAsync(
        string command,
        string relation,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();
        string commandPattern = $"%{command}%";
        string relationPattern = $"%{relation}%";

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE {commandPattern}
                      AND query ILIKE {relationPattern}
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount >= minimumCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task<User> LockUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        User[] users = await dbContext.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .ToArrayAsync(cancellationToken);
        return Assert.Single(users);
    }

    private static Task LockChallengeTableForWritesAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE email_verification_challenges IN SHARE MODE",
            cancellationToken);
    }

    private static EmailVerificationChallenge CreateChallenge(
        Guid userId,
        string emailAtIssue,
        EmailVerificationTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null)
    {
        return new EmailVerificationChallenge(
            userId,
            emailAtIssue,
            tokenHash,
            createdAt,
            expiresAt ?? createdAt.Add(TokenLifetime));
    }

    private static EmailVerificationTokenHash CreateTokenHash(byte seed)
    {
        byte[] value = Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
        return new EmailVerificationTokenHash(value);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(20));
    }

    private sealed record ChallengeState(
        string EmailAtIssue,
        EmailVerificationTokenHash TokenHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private long _utcTicks;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            SetUtcNow(utcNow);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(
                Interlocked.Read(ref _utcTicks),
                TimeSpan.Zero);
        }

        public void SetUtcNow(DateTimeOffset utcNow)
        {
            Interlocked.Exchange(
                ref _utcTicks,
                utcNow.ToUniversalTime().Ticks);
        }
    }
}
