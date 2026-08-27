using Enma.Domain.Organizations;

namespace Enma.Domain.Auditing;

/// <summary>
/// An append-only historical audit record. The MVP has no expiration or automatic purge;
/// an explicit legal/product retention decision remains a production rollout gate.
/// </summary>
public sealed class AuditLog
{
    private AuditEventDetails? _details;
    private string? _detailsJson;

    private AuditLog()
    {
    }

    private AuditLog(
        Guid id,
        Guid organizationId,
        Guid actorUserId,
        Guid actorMembershipId,
        OrganizationRole actorRoleAtOccurrence,
        AuditEventType eventType,
        AuditEntityType entityType,
        Guid entityId,
        DateTimeOffset occurredAt,
        AuditEventDetails? details,
        string? traceId)
    {
        Id = id;
        OrganizationId = organizationId;
        ActorUserId = actorUserId;
        ActorMembershipId = actorMembershipId;
        ActorRoleAtOccurrence = actorRoleAtOccurrence;
        EventType = eventType;
        EntityType = entityType;
        EntityId = entityId;
        OccurredAt = occurredAt;
        Details = details;
        TraceId = traceId;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ActorUserId { get; private set; }

    public Guid ActorMembershipId { get; private set; }

    public OrganizationRole ActorRoleAtOccurrence { get; private set; }

    public AuditEventType EventType { get; private set; }

    public AuditEntityType EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public AuditEventDetails? Details
    {
        get => _details ??= AuditEventDetails.Deserialize(EventType, _detailsJson);
        private set
        {
            _details = value;
            _detailsJson = AuditEventDetails.Serialize(value);
        }
    }

    public string? TraceId { get; private set; }

    /// <summary>
    /// Creates a historical record from context already revalidated by the authoritative
    /// transaction boundary. Semantic mutation code should create an Application audit intent.
    /// </summary>
    internal static AuditLog CreateAuthoritative(
        Guid id,
        Guid organizationId,
        Guid actorUserId,
        Guid actorMembershipId,
        OrganizationRole actorRoleAtOccurrence,
        AuditEventType eventType,
        AuditEntityType entityType,
        Guid entityId,
        DateTimeOffset occurredAt,
        AuditEventDetails? details = null,
        string? traceId = null)
    {
        ValidateRequiredIdentifier(id, nameof(id), AuditLogErrors.IdRequired);
        ValidateRequiredIdentifier(
            organizationId,
            nameof(organizationId),
            AuditLogErrors.OrganizationIdRequired);
        ValidateRequiredIdentifier(
            actorUserId,
            nameof(actorUserId),
            AuditLogErrors.ActorUserIdRequired);
        ValidateRequiredIdentifier(
            actorMembershipId,
            nameof(actorMembershipId),
            AuditLogErrors.ActorMembershipIdRequired);

        if (!Enum.IsDefined(actorRoleAtOccurrence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(actorRoleAtOccurrence),
                AuditLogErrors.ActorRoleInvalid);
        }

        if (!Enum.IsDefined(eventType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventType),
                AuditLogErrors.EventTypeInvalid);
        }

        if (!Enum.IsDefined(entityType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(entityType),
                AuditLogErrors.EntityTypeInvalid);
        }

        if (eventType.GetEntityType() != entityType)
        {
            throw new ArgumentException(
                AuditLogErrors.EventEntityTypeMismatch,
                nameof(entityType));
        }

        ValidateRequiredIdentifier(
            entityId,
            nameof(entityId),
            AuditLogErrors.EntityIdRequired);

        if (occurredAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAt),
                AuditLogErrors.OccurredAtInvalid);
        }

        eventType.ValidateDetails(details);

        return new AuditLog(
            id,
            organizationId,
            actorUserId,
            actorMembershipId,
            actorRoleAtOccurrence,
            eventType,
            entityType,
            entityId,
            occurredAt.ToUniversalTime(),
            details,
            ValidateTraceId(traceId));
    }

    private static void ValidateRequiredIdentifier(
        Guid identifier,
        string parameterName,
        string error)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(error, parameterName);
        }
    }

    private static string? ValidateTraceId(string? traceId)
    {
        if (traceId is null)
        {
            return null;
        }

        bool hasValidFormat = traceId.Length == 32 &&
            traceId.Any(character => character != '0') &&
            traceId.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        if (!hasValidFormat)
        {
            throw new ArgumentException(
                AuditLogErrors.TraceIdInvalid,
                nameof(traceId));
        }

        return traceId;
    }
}
