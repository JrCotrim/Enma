using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class EmailVerificationChallengePersistence
    : IEmailVerificationChallengePersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public EmailVerificationChallengePersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<EmailVerificationChallengeIssuancePersistenceResult>
        TryIssueOrRotateAsync(
            Guid userId,
            EmailVerificationTokenHash tokenHash,
            TimeSpan tokenLifetime,
            TimeSpan resendCooldown,
            CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user identifier is required.", nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(tokenHash);

        if (tokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenLifetime),
                "The token lifetime must be greater than zero.");
        }

        if (resendCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resendCooldown),
                "The resend cooldown cannot be negative.");
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || !user.IsActive || user.EmailVerifiedAt is not null)
        {
            return await RejectIssuanceAsync(transaction, cancellationToken);
        }

        EmailVerificationChallenge? challenge =
            (await dbContext.EmailVerificationChallenges
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM email_verification_challenges
                    WHERE user_id = {userId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (challenge is not null &&
            string.Equals(
                challenge.EmailAtIssue,
                user.Email,
                StringComparison.Ordinal) &&
            IsInsideCooldown(challenge.CreatedAt, resendCooldown, now))
        {
            return await RejectIssuanceAsync(transaction, cancellationToken);
        }

        DateTimeOffset expiresAt = AddTokenLifetime(now, tokenLifetime);

        if (challenge is null)
        {
            challenge = new EmailVerificationChallenge(
                user.Id,
                user.Email,
                tokenHash,
                now,
                expiresAt);
            await dbContext.EmailVerificationChallenges.AddAsync(
                challenge,
                cancellationToken);
        }
        else
        {
            challenge.Rotate(
                user.Email,
                tokenHash,
                now,
                expiresAt);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EmailVerificationChallengeIssuancePersistenceResult
            .CreateSucceeded(challenge.EmailAtIssue);
    }

    public async Task<EmailVerificationChallengeConsumptionPersistenceResult>
        TryConsumeAsync(
            EmailVerificationTokenHash tokenHash,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);

        Guid? candidateUserId = await dbContext.EmailVerificationChallenges
            .AsNoTracking()
            .Where(challenge => challenge.TokenHash.Equals(tokenHash))
            .Select(challenge => (Guid?)challenge.UserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (candidateUserId is null)
        {
            return EmailVerificationChallengeConsumptionPersistenceResult.Rejected;
        }

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM users
                    WHERE id = {candidateUserId.Value}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        EmailVerificationChallenge? challenge =
            (await dbContext.EmailVerificationChallenges
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM email_verification_challenges
                    WHERE user_id = {candidateUserId.Value}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || challenge is null)
        {
            return await RejectConsumptionAsync(transaction, cancellationToken);
        }

        if (!challenge.TokenHash.Equals(tokenHash))
        {
            return await RejectConsumptionAsync(transaction, cancellationToken);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool emailMatches = string.Equals(
            challenge.EmailAtIssue,
            user.Email,
            StringComparison.Ordinal);

        if (!user.IsActive ||
            user.EmailVerifiedAt is not null ||
            !emailMatches ||
            challenge.IsExpired(now))
        {
            dbContext.EmailVerificationChallenges.Remove(challenge);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EmailVerificationChallengeConsumptionPersistenceResult.Rejected;
        }

        user.VerifyEmail(now);
        dbContext.EmailVerificationChallenges.Remove(challenge);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EmailVerificationChallengeConsumptionPersistenceResult.Succeeded;
    }

    private static DateTimeOffset AddTokenLifetime(
        DateTimeOffset now,
        TimeSpan tokenLifetime)
    {
        if (tokenLifetime > DateTimeOffset.MaxValue - now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenLifetime),
                "The token lifetime exceeds the supported timestamp range.");
        }

        return now.Add(tokenLifetime);
    }

    private static bool IsInsideCooldown(
        DateTimeOffset createdAt,
        TimeSpan resendCooldown,
        DateTimeOffset now)
    {
        if (resendCooldown > DateTimeOffset.MaxValue - createdAt)
        {
            return true;
        }

        return now < createdAt.Add(resendCooldown);
    }

    private static async Task<EmailVerificationChallengeIssuancePersistenceResult>
        RejectIssuanceAsync(
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return EmailVerificationChallengeIssuancePersistenceResult.Rejected;
    }

    private static async Task<EmailVerificationChallengeConsumptionPersistenceResult>
        RejectConsumptionAsync(
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return EmailVerificationChallengeConsumptionPersistenceResult.Rejected;
    }
}
