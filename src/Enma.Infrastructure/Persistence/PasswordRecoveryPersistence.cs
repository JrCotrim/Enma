using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class PasswordRecoveryPersistence : IPasswordRecoveryPersistence
{
    private readonly DbContextOptions<EnmaDbContext> dbContextOptions;
    private readonly TimeProvider timeProvider;

    public PasswordRecoveryPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.dbContextOptions = dbContextOptions;
        this.timeProvider = timeProvider;
    }

    public async Task<PasswordRecoveryChallengeIssuanceResult> TryIssueOrRotateAsync(
        string normalizedEmail,
        PasswordRecoveryTokenHash tokenHash,
        TimeSpan tokenLifetime,
        TimeSpan resendCooldown,
        CancellationToken cancellationToken = default)
    {
        string canonicalEmail = User.NormalizeEmail(normalizedEmail);

        if (!string.Equals(normalizedEmail, canonicalEmail, StringComparison.Ordinal))
        {
            throw new ArgumentException("The email must be normalized.", nameof(normalizedEmail));
        }

        ArgumentNullException.ThrowIfNull(tokenHash);

        if (tokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenLifetime));
        }

        if (resendCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resendCooldown));
        }

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE email = {normalizedEmail} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || !user.IsActive || user.EmailVerifiedAt is null)
        {
            return await RejectIssuanceAsync(transaction, cancellationToken);
        }

        UserCredential? credential = (await dbContext.UserCredentials
                .FromSqlInterpolated(
                    $"SELECT * FROM user_credentials WHERE user_id = {user.Id} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (credential is null)
        {
            return await RejectIssuanceAsync(transaction, cancellationToken);
        }

        PasswordRecoveryChallenge? challenge = (await dbContext.PasswordRecoveryChallenges
                .FromSqlInterpolated(
                    $"SELECT * FROM password_recovery_challenges WHERE user_id = {user.Id} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (challenge is not null && now < AddSafely(challenge.CreatedAt, resendCooldown))
        {
            return await RejectIssuanceAsync(transaction, cancellationToken);
        }

        DateTimeOffset expiresAt = AddSafely(now, tokenLifetime);

        if (challenge is null)
        {
            await dbContext.PasswordRecoveryChallenges.AddAsync(
                new PasswordRecoveryChallenge(
                    user.Id,
                    user.Email,
                    tokenHash,
                    now,
                    expiresAt),
                cancellationToken);
        }
        else
        {
            challenge.Rotate(user.Email, tokenHash, now, expiresAt);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PasswordRecoveryChallengeIssuanceResult.CreateSucceeded(user.Email);
    }

    public async Task<PasswordRecoveryResetPersistenceResult> TryResetPasswordAsync(
        PasswordRecoveryTokenHash tokenHash,
        string newPasswordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        Guid? candidateUserId = await dbContext.PasswordRecoveryChallenges
            .AsNoTracking()
            .Where(challenge => challenge.TokenHash.Equals(tokenHash))
            .Select(challenge => (Guid?)challenge.UserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (candidateUserId is null)
        {
            return PasswordRecoveryResetPersistenceResult.Rejected;
        }

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {candidateUserId.Value} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        UserCredential? credential = (await dbContext.UserCredentials
                .FromSqlInterpolated(
                    $"SELECT * FROM user_credentials WHERE user_id = {candidateUserId.Value} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
        PasswordRecoveryChallenge? challenge = (await dbContext.PasswordRecoveryChallenges
                .FromSqlInterpolated(
                    $"SELECT * FROM password_recovery_challenges WHERE user_id = {candidateUserId.Value} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || credential is null || challenge is null ||
            !challenge.TokenHash.Equals(tokenHash))
        {
            return await RejectResetAsync(transaction, cancellationToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        bool valid = user.IsActive &&
            user.EmailVerifiedAt is not null &&
            string.Equals(challenge.EmailAtIssue, user.Email, StringComparison.Ordinal) &&
            !challenge.IsExpired(now);

        if (!valid)
        {
            dbContext.PasswordRecoveryChallenges.Remove(challenge);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PasswordRecoveryResetPersistenceResult.Rejected;
        }

        credential.ChangePasswordHash(newPasswordHash, now);
        dbContext.PasswordRecoveryChallenges.Remove(challenge);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PasswordRecoveryResetPersistenceResult.Succeeded;
    }

    private static DateTimeOffset AddSafely(DateTimeOffset timestamp, TimeSpan duration) =>
        duration > DateTimeOffset.MaxValue - timestamp
            ? DateTimeOffset.MaxValue
            : timestamp.Add(duration);

    private static async Task<PasswordRecoveryChallengeIssuanceResult>
        RejectIssuanceAsync(
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return PasswordRecoveryChallengeIssuanceResult.Rejected;
    }

    private static async Task<PasswordRecoveryResetPersistenceResult>
        RejectResetAsync(
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return PasswordRecoveryResetPersistenceResult.Rejected;
    }
}
