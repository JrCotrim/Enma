using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class AuthenticationSessionIssuancePersistence
    : IAuthenticationSessionIssuancePersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public AuthenticationSessionIssuancePersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<AuthenticationSessionIssuancePersistenceResult> TryPersistAsync(
        AuthenticationSession session,
        string? upgradedPasswordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = (await dbContext.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {session.UserId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (user is null || !user.IsActive || user.EmailVerifiedAt is null)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        UserCredential? credential = (await dbContext.UserCredentials
                .FromSqlInterpolated(
                    $"SELECT * FROM user_credentials WHERE user_id = {session.UserId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (credential is null ||
            credential.CredentialVersion != session.CredentialVersionAtIssue)
        {
            return await RejectAsync(transaction, cancellationToken);
        }

        if (upgradedPasswordHash is not null)
        {
            credential.UpgradePasswordHash(upgradedPasswordHash);
        }

        await dbContext.AuthenticationSessions.AddAsync(
            session,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return AuthenticationSessionIssuancePersistenceResult.Succeeded;
    }

    private static async Task<AuthenticationSessionIssuancePersistenceResult>
        RejectAsync(
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return AuthenticationSessionIssuancePersistenceResult.Rejected;
    }
}
