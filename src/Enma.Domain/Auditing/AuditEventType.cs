namespace Enma.Domain.Auditing;

/// <summary>
/// Identifies the semantic meaning of an audit record.
/// Numeric values and text codes are permanent. Only append new values; never reuse one.
/// </summary>
public enum AuditEventType
{
    OrganizationRenamed = 1,
    OrganizationMembershipRoleChanged = 2,
    OrganizationMembershipDeactivated = 3,
    OrganizationMembershipReactivated = 4,
    ClientCreated = 5,
    ClientRenamed = 6,
    ClientDeactivated = 7,
    ClientReactivated = 8,
    LegalProcessCreated = 9,
    LegalProcessTitleChanged = 10,
    LegalDeadlineCreated = 11,
    LegalDeadlineDetailsChanged = 12,
    LegalDeadlineCompleted = 13,
    LegalDeadlineReopened = 14,
    LegalTaskCreated = 15,
    LegalTaskDetailsChanged = 16,
    LegalTaskAssigneeChanged = 17,
    LegalTaskCompleted = 18,
    LegalTaskReopened = 19,
    CalendarEventCreated = 20,
    CalendarEventUpdated = 21,
    CalendarEventAssigneeChanged = 22,
    CalendarEventDeleted = 23,
    LegalDocumentUploaded = 24,
    /// <summary>
    /// The actor is the live administrative Membership revalidated by the
    /// authoritative creation transaction.
    /// </summary>
    OrganizationInvitationCreated = 25,
    /// <summary>
    /// The actor is the live administrative Membership revalidated with the
    /// target invitation and its role by the authoritative revoke transaction.
    /// </summary>
    OrganizationInvitationRevoked = 26,
    /// <summary>
    /// The actor is the resulting active Membership created or reactivated by
    /// acceptance in the authoritative transaction, never the invitation creator.
    /// </summary>
    OrganizationInvitationAccepted = 27,
    /// <summary>
    /// The actor is the live administrative Membership revalidated with the
    /// target invitation and its role by the authoritative resend transaction.
    /// </summary>
    OrganizationInvitationResent = 28,
    ClientProfileUpdated = 29
}

public static class AuditEventTypeExtensions
{
    public static string ToCode(this AuditEventType eventType)
    {
        return eventType switch
        {
            AuditEventType.OrganizationRenamed => "organization.renamed",
            AuditEventType.OrganizationMembershipRoleChanged =>
                "organization_membership.role_changed",
            AuditEventType.OrganizationMembershipDeactivated =>
                "organization_membership.deactivated",
            AuditEventType.OrganizationMembershipReactivated =>
                "organization_membership.reactivated",
            AuditEventType.ClientCreated => "client.created",
            AuditEventType.ClientRenamed => "client.renamed",
            AuditEventType.ClientDeactivated => "client.deactivated",
            AuditEventType.ClientReactivated => "client.reactivated",
            AuditEventType.LegalProcessCreated => "legal_process.created",
            AuditEventType.LegalProcessTitleChanged => "legal_process.title_changed",
            AuditEventType.LegalDeadlineCreated => "legal_deadline.created",
            AuditEventType.LegalDeadlineDetailsChanged =>
                "legal_deadline.details_changed",
            AuditEventType.LegalDeadlineCompleted => "legal_deadline.completed",
            AuditEventType.LegalDeadlineReopened => "legal_deadline.reopened",
            AuditEventType.LegalTaskCreated => "legal_task.created",
            AuditEventType.LegalTaskDetailsChanged => "legal_task.details_changed",
            AuditEventType.LegalTaskAssigneeChanged => "legal_task.assignee_changed",
            AuditEventType.LegalTaskCompleted => "legal_task.completed",
            AuditEventType.LegalTaskReopened => "legal_task.reopened",
            AuditEventType.CalendarEventCreated => "calendar_event.created",
            AuditEventType.CalendarEventUpdated => "calendar_event.updated",
            AuditEventType.CalendarEventAssigneeChanged =>
                "calendar_event.assignee_changed",
            AuditEventType.CalendarEventDeleted => "calendar_event.deleted",
            AuditEventType.LegalDocumentUploaded => "legal_document.uploaded",
            AuditEventType.OrganizationInvitationCreated =>
                "organization_invitation.created",
            AuditEventType.OrganizationInvitationRevoked =>
                "organization_invitation.revoked",
            AuditEventType.OrganizationInvitationAccepted =>
                "organization_invitation.accepted",
            AuditEventType.OrganizationInvitationResent =>
                "organization_invitation.resent",
            AuditEventType.ClientProfileUpdated =>
                "client.profile_updated",
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                AuditLogErrors.EventTypeInvalid)
        };
    }

    public static AuditEntityType GetEntityType(this AuditEventType eventType)
    {
        return eventType switch
        {
            AuditEventType.OrganizationRenamed => AuditEntityType.Organization,
            AuditEventType.OrganizationMembershipRoleChanged or
                AuditEventType.OrganizationMembershipDeactivated or
                AuditEventType.OrganizationMembershipReactivated =>
                AuditEntityType.OrganizationMembership,
            AuditEventType.ClientCreated or
                AuditEventType.ClientRenamed or
                AuditEventType.ClientDeactivated or
                AuditEventType.ClientReactivated or
                AuditEventType.ClientProfileUpdated => AuditEntityType.Client,
            AuditEventType.LegalProcessCreated or
                AuditEventType.LegalProcessTitleChanged => AuditEntityType.LegalProcess,
            AuditEventType.LegalDeadlineCreated or
                AuditEventType.LegalDeadlineDetailsChanged or
                AuditEventType.LegalDeadlineCompleted or
                AuditEventType.LegalDeadlineReopened => AuditEntityType.LegalDeadline,
            AuditEventType.LegalTaskCreated or
                AuditEventType.LegalTaskDetailsChanged or
                AuditEventType.LegalTaskAssigneeChanged or
                AuditEventType.LegalTaskCompleted or
                AuditEventType.LegalTaskReopened => AuditEntityType.LegalTask,
            AuditEventType.CalendarEventCreated or
                AuditEventType.CalendarEventUpdated or
                AuditEventType.CalendarEventAssigneeChanged or
                AuditEventType.CalendarEventDeleted => AuditEntityType.CalendarEvent,
            AuditEventType.LegalDocumentUploaded => AuditEntityType.LegalDocument,
            AuditEventType.OrganizationInvitationCreated or
                AuditEventType.OrganizationInvitationRevoked or
                AuditEventType.OrganizationInvitationAccepted or
                AuditEventType.OrganizationInvitationResent =>
                AuditEntityType.OrganizationInvitation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                AuditLogErrors.EventTypeInvalid)
        };
    }

    public static void ValidateDetails(
        this AuditEventType eventType,
        AuditEventDetails? details)
    {
        Type? expectedDetailsType = eventType switch
        {
            AuditEventType.OrganizationRenamed =>
                typeof(OrganizationRenamedAuditDetails),
            AuditEventType.OrganizationMembershipRoleChanged =>
                typeof(OrganizationMembershipRoleChangedAuditDetails),
            AuditEventType.LegalDeadlineDetailsChanged =>
                typeof(LegalDeadlineDetailsChangedAuditDetails),
            AuditEventType.LegalTaskDetailsChanged =>
                typeof(LegalTaskDetailsChangedAuditDetails),
            AuditEventType.LegalTaskAssigneeChanged =>
                typeof(LegalTaskAssigneeChangedAuditDetails),
            AuditEventType.CalendarEventUpdated =>
                typeof(CalendarEventUpdatedAuditDetails),
            AuditEventType.CalendarEventAssigneeChanged =>
                typeof(CalendarEventAssigneeChangedAuditDetails),
            AuditEventType.OrganizationInvitationCreated =>
                typeof(OrganizationInvitationCreatedAuditDetails),
            AuditEventType.OrganizationMembershipDeactivated or
                AuditEventType.OrganizationMembershipReactivated or
                AuditEventType.ClientCreated or
                AuditEventType.ClientRenamed or
                AuditEventType.ClientDeactivated or
                AuditEventType.ClientReactivated or
                AuditEventType.ClientProfileUpdated or
                AuditEventType.LegalProcessCreated or
                AuditEventType.LegalProcessTitleChanged or
                AuditEventType.LegalDeadlineCreated or
                AuditEventType.LegalDeadlineCompleted or
                AuditEventType.LegalDeadlineReopened or
                AuditEventType.LegalTaskCreated or
                AuditEventType.LegalTaskCompleted or
                AuditEventType.LegalTaskReopened or
                AuditEventType.CalendarEventCreated or
                AuditEventType.CalendarEventDeleted or
                AuditEventType.LegalDocumentUploaded or
                AuditEventType.OrganizationInvitationRevoked or
                AuditEventType.OrganizationInvitationAccepted or
                AuditEventType.OrganizationInvitationResent => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventType),
                AuditLogErrors.EventTypeInvalid)
        };

        if (expectedDetailsType is null && details is null ||
            expectedDetailsType is not null && details?.GetType() == expectedDetailsType)
        {
            if (details is not null)
            {
                AuditEventDetails.ValidateSerializedSize(details);
            }

            return;
        }

        throw new ArgumentException(
            AuditLogErrors.DetailsInvalidForEventType,
            nameof(details));
    }
}
