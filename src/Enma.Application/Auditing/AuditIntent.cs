using Enma.Domain.Auditing;

namespace Enma.Application.Auditing;

/// <summary>
/// Describes only the semantic result of an effective mutation. Authoritative actor,
/// tenant, occurrence time, and trace context belong to the persistence transaction.
/// </summary>
public sealed class AuditIntent
{
    public AuditIntent(
        AuditEventType eventType,
        Guid entityId,
        AuditEventDetails? details = null)
    {
        if (entityId == Guid.Empty)
        {
            throw new ArgumentException(
                AuditLogErrors.EntityIdRequired,
                nameof(entityId));
        }

        eventType.ValidateDetails(details);

        EventType = eventType;
        EntityType = eventType.GetEntityType();
        EntityId = entityId;
        Details = details;
    }

    public AuditEventType EventType { get; }

    public AuditEntityType EntityType { get; }

    public Guid EntityId { get; }

    public AuditEventDetails? Details { get; }
}
