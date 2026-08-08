using Enma.Application.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

public sealed class EmailVerificationUserLookup : IEmailVerificationUserLookup
{
    private readonly EnmaDbContext _dbContext;

    public EmailVerificationUserLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<Guid?> FindUserIdByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Email == normalizedEmail)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
