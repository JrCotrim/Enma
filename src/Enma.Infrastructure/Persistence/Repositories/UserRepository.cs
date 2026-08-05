using Enma.Application.Users;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly EnmaDbContext _dbContext;

    public UserRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<bool> ExistsByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Email == email,
                cancellationToken);
    }

    public async Task AddAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Users.AddAsync(user, cancellationToken);
    }
}
