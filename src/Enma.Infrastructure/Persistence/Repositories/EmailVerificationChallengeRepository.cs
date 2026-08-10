using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.Infrastructure.Persistence.Repositories;

public sealed class EmailVerificationChallengeRepository
    : IEmailVerificationChallengeRepository
{
    private readonly EnmaDbContext _dbContext;

    public EmailVerificationChallengeRepository(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task AddAsync(
        EmailVerificationChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.EmailVerificationChallenges.AddAsync(
            challenge,
            cancellationToken);
    }
}
