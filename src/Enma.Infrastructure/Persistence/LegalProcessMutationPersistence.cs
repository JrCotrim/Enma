using System.Data;
using Enma.Application.Processes;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalProcessMutationPersistence
    : ILegalProcessMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public LegalProcessMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        Guid processId,
        Guid organizationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalProcess? legalProcess = (await dbContext.LegalProcesses
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_processes
                    WHERE id = {processId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (legalProcess is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalProcessMutationPersistenceResult.NotFound;
        }

        legalProcess.ChangeTitle(title);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalProcessMutationPersistenceResult.Updated;
    }
}
