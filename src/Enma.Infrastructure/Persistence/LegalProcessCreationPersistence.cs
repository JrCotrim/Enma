using Enma.Application.Processes;
using Enma.Domain.Processes;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalProcessCreationPersistence
    : ILegalProcessCreationPersistence
{
    private readonly EnmaDbContext _dbContext;

    public LegalProcessCreationPersistence(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task PersistAsync(
        LegalProcess legalProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(legalProcess);

        await _dbContext.LegalProcesses.AddAsync(legalProcess, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
