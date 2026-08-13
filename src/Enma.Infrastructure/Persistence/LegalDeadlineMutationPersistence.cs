using System.Data;
using Enma.Application.Deadlines;
using Enma.Domain.Deadlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class LegalDeadlineMutationPersistence
    : ILegalDeadlineMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public LegalDeadlineMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<LegalDeadlineDetailsMutationPersistenceResult>
        UpdateDetailsAsync(
            Guid deadlineId,
            Guid organizationId,
            string title,
            DateOnly dueDate,
            CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalDeadline? legalDeadline = await LockDeadlineAsync(
            dbContext,
            deadlineId,
            organizationId,
            cancellationToken);

        if (legalDeadline is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineDetailsMutationPersistenceResult.NotFound;
        }

        if (legalDeadline.CompletedAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineDetailsMutationPersistenceResult.Conflict;
        }

        legalDeadline.ChangeDetails(title, dueDate);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDeadlineDetailsMutationPersistenceResult.Updated;
    }

    public async Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        Guid deadlineId,
        Guid organizationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalDeadline? legalDeadline = await LockDeadlineAsync(
            dbContext,
            deadlineId,
            organizationId,
            cancellationToken);

        if (legalDeadline is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineLifecycleMutationPersistenceResult.NotFound;
        }

        legalDeadline.Complete(completedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDeadlineLifecycleMutationPersistenceResult.Succeeded;
    }

    public async Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        LegalDeadline? legalDeadline = await LockDeadlineAsync(
            dbContext,
            deadlineId,
            organizationId,
            cancellationToken);

        if (legalDeadline is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return LegalDeadlineLifecycleMutationPersistenceResult.NotFound;
        }

        legalDeadline.Reopen();
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return LegalDeadlineLifecycleMutationPersistenceResult.Succeeded;
    }

    private static async Task<LegalDeadline?> LockDeadlineAsync(
        EnmaDbContext dbContext,
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.LegalDeadlines
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM legal_deadlines
                    WHERE id = {deadlineId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }
}
