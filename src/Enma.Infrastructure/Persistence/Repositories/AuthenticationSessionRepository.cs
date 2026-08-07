using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class AuthenticationSessionRepository
    : IAuthenticationSessionRepository
{
    private readonly EnmaDbContext _dbContext;

    public AuthenticationSessionRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<AuthenticationSession?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.AuthenticationSessions.SingleOrDefaultAsync(
            session => session.Id == id,
            cancellationToken);
    }

    public Task<AuthenticationSession?> GetBySecretHashAsync(
        AuthenticationSessionSecretHash secretHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretHash);

        return _dbContext.AuthenticationSessions.SingleOrDefaultAsync(
            session => session.SecretHash == secretHash,
            cancellationToken);
    }

    public async Task AddAsync(
        AuthenticationSession session,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.AuthenticationSessions.AddAsync(
            session,
            cancellationToken);
    }
}
