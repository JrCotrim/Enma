using Enma.Application.Deadlines;
using Enma.Domain.Deadlines;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDeadlineCreationPersistence
    : ILegalDeadlineCreationPersistence
{
    private readonly EnmaDbContext _dbContext;

    public LegalDeadlineCreationPersistence(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task PersistAsync(
        LegalDeadline legalDeadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(legalDeadline);

        await _dbContext.LegalDeadlines.AddAsync(legalDeadline, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
