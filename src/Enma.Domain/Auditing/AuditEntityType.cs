namespace Enma.Domain.Auditing;

/// <summary>
/// Identifies the subject type of an audit record.
/// Numeric values and text codes are permanent. Only append new values; never reuse one.
/// </summary>
public enum AuditEntityType
{
    Organization = 1,
    OrganizationMembership = 2,
    Client = 3,
    LegalProcess = 4,
    LegalDeadline = 5,
    LegalTask = 6,
    CalendarEvent = 7,
    LegalDocument = 8,
    OrganizationInvitation = 9
}

public static class AuditEntityTypeExtensions
{
    public static string ToCode(this AuditEntityType entityType)
    {
        return entityType switch
        {
            AuditEntityType.Organization => "organization",
            AuditEntityType.OrganizationMembership => "organization_membership",
            AuditEntityType.Client => "client",
            AuditEntityType.LegalProcess => "legal_process",
            AuditEntityType.LegalDeadline => "legal_deadline",
            AuditEntityType.LegalTask => "legal_task",
            AuditEntityType.CalendarEvent => "calendar_event",
            AuditEntityType.LegalDocument => "legal_document",
            AuditEntityType.OrganizationInvitation => "organization_invitation",
            _ => throw new ArgumentOutOfRangeException(
                nameof(entityType),
                AuditLogErrors.EntityTypeInvalid)
        };
    }
}
