using Enma.Application.Users;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class UserCredentialRepository : IUserCredentialRepository
{
    private readonly EnmaDbContext _dbContext;

    public UserCredentialRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<UserCredential?> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.UserCredentials.SingleOrDefaultAsync(
            credential => credential.UserId == userId,
            cancellationToken);
    }

    public async Task AddAsync(
        UserCredential credential,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.UserCredentials.AddAsync(
            credential,
            cancellationToken);
    }
}
