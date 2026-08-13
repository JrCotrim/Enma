using Enma.Application.Deadlines;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalDeadlineReadQueries : ILegalDeadlineReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public LegalDeadlineReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalDeadlineDetailReadModel?> FindAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LegalDeadlineDetailReadModel> query =
            from legalDeadline in _dbContext.LegalDeadlines.AsNoTracking()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalDeadline.OrganizationId,
                    ProcessId = legalDeadline.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = legalProcess.Id
                }
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            where legalDeadline.Id == deadlineId &&
                legalDeadline.OrganizationId == organizationId
            select new LegalDeadlineDetailReadModel(
                legalDeadline.Id,
                legalDeadline.Title,
                legalDeadline.DueDate,
                legalDeadline.ProcessId,
                legalProcess.Title,
                client.Name,
                legalDeadline.CompletedAt == null
                    ? LegalDeadlineReadState.Pending
                    : LegalDeadlineReadState.Completed,
                legalDeadline.CreatedAt,
                legalDeadline.CompletedAt);

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int skippedItems = checked((pageNumber - 1) * pageSize);

        IQueryable<LegalDeadlineListItem> query =
            from legalDeadline in _dbContext.LegalDeadlines.AsNoTracking()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalDeadline.OrganizationId,
                    ProcessId = legalDeadline.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = legalProcess.Id
                }
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            where legalDeadline.OrganizationId == organizationId
            orderby legalDeadline.DueDate, legalDeadline.Id
            select new LegalDeadlineListItem(
                legalDeadline.Id,
                legalDeadline.Title,
                legalDeadline.DueDate,
                legalDeadline.ProcessId,
                legalProcess.Title,
                client.Name,
                legalDeadline.CompletedAt == null
                    ? LegalDeadlineReadState.Pending
                    : LegalDeadlineReadState.Completed);

        return await query
            .Skip(skippedItems)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
    }
}
