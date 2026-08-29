using Enma.Application.Auditing.List;
using Enma.Domain.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class AuditLogReadQueries : IAuditLogReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public AuditLogReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<AuditLogReadPage> ListAsync(
        AuditLogReadQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        int skippedItems = checked((query.PageNumber - 1) * query.PageSize);
        IQueryable<AuditLog> auditLogs = _dbContext.AuditLogs
            .AsNoTracking()
            .Where(auditLog => auditLog.OrganizationId == query.OrganizationId);

        if (query.EventType is AuditEventType eventType)
        {
            auditLogs = auditLogs.Where(auditLog =>
                auditLog.EventType == eventType);
        }

        if (query.EntityType is AuditEntityType entityType &&
            query.EntityId is Guid entityId)
        {
            auditLogs = auditLogs.Where(auditLog =>
                auditLog.EntityType == entityType &&
                auditLog.EntityId == entityId);
        }

        int totalCount = await auditLogs.CountAsync(cancellationToken);
        AuditLog[] page = await auditLogs
            .OrderByDescending(auditLog => auditLog.OccurredAt)
            .ThenByDescending(auditLog => auditLog.Id)
            .Skip(skippedItems)
            .Take(query.PageSize)
            .ToArrayAsync(cancellationToken);

        AuditLogReadModel[] items = page
            .Select(auditLog => new AuditLogReadModel(
                auditLog.Id,
                auditLog.ActorMembershipId,
                auditLog.ActorRoleAtOccurrence,
                auditLog.EventType,
                auditLog.EntityType,
                auditLog.EntityId,
                auditLog.OccurredAt,
                auditLog.Details))
            .ToArray();

        return new AuditLogReadPage(items, totalCount);
    }
}
