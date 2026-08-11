using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class AuthenticationSessionRuntimePersistence
    : IAuthenticationSessionRuntimePersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public AuthenticationSessionRuntimePersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<Guid?> TryValidateAndRenewAsync(
        AuthenticationSessionSecretHash secretHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretHash);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        SessionLocator? locator = await dbContext.AuthenticationSessions
            .AsNoTracking()
            .Where(session => session.SecretHash == secretHash)
            .Select(session => new SessionLocator(session.Id, session.UserId))
            .SingleOrDefaultAsync(cancellationToken);

        if (locator is null)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {locator.UserId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || !user.IsActive)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        UserCredential? credential = (await dbContext.UserCredentials
                .FromSqlInterpolated(
                    $"SELECT * FROM user_credentials WHERE user_id = {locator.UserId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (credential is null)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        AuthenticationSession? session = (await dbContext.AuthenticationSessions
                .FromSqlInterpolated(
                    $"SELECT * FROM authentication_sessions WHERE id = {locator.Id} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (session is null ||
            !session.SecretHash.Equals(secretHash) ||
            session.UserId != user.Id ||
            session.RevokedAt is not null ||
            now >= session.IdleExpiresAt ||
            now >= session.AbsoluteExpiresAt ||
            session.CredentialVersionAtIssue != credential.CredentialVersion)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        DateTimeOffset candidateIdleExpiresAt = now >
            DateTimeOffset.MaxValue.Subtract(AuthenticationSessionPolicy.IdleLifetime)
                ? DateTimeOffset.MaxValue
                : now.Add(AuthenticationSessionPolicy.IdleLifetime);
        DateTimeOffset renewedIdleExpiresAt = candidateIdleExpiresAt;

        if (renewedIdleExpiresAt < session.IdleExpiresAt)
        {
            renewedIdleExpiresAt = session.IdleExpiresAt;
        }

        if (renewedIdleExpiresAt > session.AbsoluteExpiresAt)
        {
            renewedIdleExpiresAt = session.AbsoluteExpiresAt;
        }

        DateTimeOffset seenAt = now < session.LastSeenAt
            ? session.LastSeenAt
            : now;

        session.Touch(seenAt, renewedIdleExpiresAt);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return user.Id;
    }

    private static async Task<Guid?> RejectAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return null;
    }

    private sealed record SessionLocator(Guid Id, Guid UserId);
}
