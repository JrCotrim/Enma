using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class AuthenticationSessionRevocationPersistence
    : IAuthenticationSessionRevocationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public AuthenticationSessionRevocationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task RevokeAsync(
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

        byte[] secretHashValue = secretHash.ToArray();
        AuthenticationSession? session = (await dbContext.AuthenticationSessions
                .FromSqlInterpolated(
                    $"SELECT * FROM authentication_sessions WHERE secret_hash = {secretHashValue} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (session is not null && session.RevokedAt is null)
        {
            session.Revoke(now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
