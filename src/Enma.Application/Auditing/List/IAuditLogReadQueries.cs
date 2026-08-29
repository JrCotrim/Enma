using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.Application.Auditing.List;

public interface IAuditLogReadQueries
{
    Task<AuditLogReadPage> ListAsync(
        AuditLogReadQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record AuditLogReadQuery(
    Guid OrganizationId,
    AuditEventType? EventType,
    AuditEntityType? EntityType,
    Guid? EntityId,
    int PageNumber,
    int PageSize);

public sealed record AuditLogReadPage(
    IReadOnlyList<AuditLogReadModel> Items,
    int TotalCount);

public sealed record AuditLogReadModel(
    Guid Id,
    Guid ActorMembershipId,
    OrganizationRole ActorRoleAtOccurrence,
    AuditEventType EventType,
    AuditEntityType EntityType,
    Guid EntityId,
    DateTimeOffset OccurredAt,
    AuditEventDetails? Details);
