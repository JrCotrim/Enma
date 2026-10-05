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

        // The actor is resolved through the event's own organization and
        // membership pair, never through a bare membership or user id.
        var page = await (
            from auditLog in auditLogs
            join membership in _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    auditLog.OrganizationId,
                    MembershipId = auditLog.ActorMembershipId
                }
                equals new
                {
                    membership.OrganizationId,
                    MembershipId = membership.Id
                }
                into actorMemberships
            from actorMembership in actorMemberships.DefaultIfEmpty()
            join user in _dbContext.Users.AsNoTracking()
                on actorMembership!.UserId equals user.Id
                into actorUsers
            from actorUser in actorUsers.DefaultIfEmpty()
            orderby auditLog.OccurredAt descending, auditLog.Id descending
            select new
            {
                AuditLog = auditLog,
                ActorDisplayName = actorUser == null ? null : actorUser.Name,
                ActorMembershipActive = actorMembership == null
                    ? (bool?)null
                    : actorMembership.IsActive
            })
            .Skip(skippedItems)
            .Take(query.PageSize)
            .ToArrayAsync(cancellationToken);

        AuditLogReadModel[] items = page
            .Select(row => new AuditLogReadModel(
                row.AuditLog.Id,
                row.AuditLog.ActorMembershipId,
                row.AuditLog.ActorRoleAtOccurrence,
                row.ActorDisplayName,
                row.ActorMembershipActive,
                row.AuditLog.EventType,
                row.AuditLog.EntityType,
                row.AuditLog.EntityId,
                row.AuditLog.OccurredAt,
                row.AuditLog.Details))
            .ToArray();

        return new AuditLogReadPage(items, totalCount);
    }
}
