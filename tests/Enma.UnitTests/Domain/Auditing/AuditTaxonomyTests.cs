using Enma.Domain.Auditing;

namespace Enma.UnitTests.Domain.Auditing;

public sealed class AuditTaxonomyTests
{
    [Theory]
    [MemberData(nameof(ExpectedEvents))]
    public void EventType_HasPermanentValueCodeAndEntityType(
        AuditEventType eventType,
        int numericValue,
        string code,
        AuditEntityType entityType)
    {
        Assert.Equal(numericValue, (int)eventType);
        Assert.Equal(code, eventType.ToCode());
        Assert.Equal(entityType, eventType.GetEntityType());
    }

    [Fact]
    public void EventType_ValuesAndCodesAreUniqueAndComplete()
    {
        AuditEventType[] values = Enum.GetValues<AuditEventType>();
        string[] codes = values.Select(value => value.ToCode()).ToArray();

        Assert.Equal(ExpectedEvents.Count, values.Length);
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EventType_UnknownValueIsRejected()
    {
        var unknown = (AuditEventType)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => unknown.ToCode());
        Assert.Throws<ArgumentOutOfRangeException>(() => unknown.GetEntityType());
    }

    [Theory]
    [MemberData(nameof(ExpectedEntities))]
    public void EntityType_HasPermanentValueAndCode(
        AuditEntityType entityType,
        int numericValue,
        string code)
    {
        Assert.Equal(numericValue, (int)entityType);
        Assert.Equal(code, entityType.ToCode());
    }

    [Fact]
    public void EntityType_ValuesAndCodesAreUniqueAndComplete()
    {
        AuditEntityType[] values = Enum.GetValues<AuditEntityType>();
        string[] codes = values.Select(value => value.ToCode()).ToArray();

        Assert.Equal(ExpectedEntities.Count, values.Length);
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EntityType_UnknownValueIsRejected()
    {
        var unknown = (AuditEntityType)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => unknown.ToCode());
    }

    public static TheoryData<AuditEventType, int, string, AuditEntityType>
        ExpectedEvents =>
        new()
        {
            {
                AuditEventType.OrganizationRenamed,
                1,
                "organization.renamed",
                AuditEntityType.Organization
            },
            {
                AuditEventType.OrganizationMembershipRoleChanged,
                2,
                "organization_membership.role_changed",
                AuditEntityType.OrganizationMembership
            },
            {
                AuditEventType.OrganizationMembershipDeactivated,
                3,
                "organization_membership.deactivated",
                AuditEntityType.OrganizationMembership
            },
            {
                AuditEventType.OrganizationMembershipReactivated,
                4,
                "organization_membership.reactivated",
                AuditEntityType.OrganizationMembership
            },
            {
                AuditEventType.ClientCreated,
                5,
                "client.created",
                AuditEntityType.Client
            },
            {
                AuditEventType.ClientRenamed,
                6,
                "client.renamed",
                AuditEntityType.Client
            },
            {
                AuditEventType.ClientDeactivated,
                7,
                "client.deactivated",
                AuditEntityType.Client
            },
            {
                AuditEventType.ClientReactivated,
                8,
                "client.reactivated",
                AuditEntityType.Client
            },
            {
                AuditEventType.LegalProcessCreated,
                9,
                "legal_process.created",
                AuditEntityType.LegalProcess
            },
            {
                AuditEventType.LegalProcessTitleChanged,
                10,
                "legal_process.title_changed",
                AuditEntityType.LegalProcess
            },
            {
                AuditEventType.LegalDeadlineCreated,
                11,
                "legal_deadline.created",
                AuditEntityType.LegalDeadline
            },
            {
                AuditEventType.LegalDeadlineDetailsChanged,
                12,
                "legal_deadline.details_changed",
                AuditEntityType.LegalDeadline
            },
            {
                AuditEventType.LegalDeadlineCompleted,
                13,
                "legal_deadline.completed",
                AuditEntityType.LegalDeadline
            },
            {
                AuditEventType.LegalDeadlineReopened,
                14,
                "legal_deadline.reopened",
                AuditEntityType.LegalDeadline
            },
            {
                AuditEventType.LegalTaskCreated,
                15,
                "legal_task.created",
                AuditEntityType.LegalTask
            },
            {
                AuditEventType.LegalTaskDetailsChanged,
                16,
                "legal_task.details_changed",
                AuditEntityType.LegalTask
            },
            {
                AuditEventType.LegalTaskAssigneeChanged,
                17,
                "legal_task.assignee_changed",
                AuditEntityType.LegalTask
            },
            {
                AuditEventType.LegalTaskCompleted,
                18,
                "legal_task.completed",
                AuditEntityType.LegalTask
            },
            {
                AuditEventType.LegalTaskReopened,
                19,
                "legal_task.reopened",
                AuditEntityType.LegalTask
            },
            {
                AuditEventType.CalendarEventCreated,
                20,
                "calendar_event.created",
                AuditEntityType.CalendarEvent
            },
            {
                AuditEventType.CalendarEventUpdated,
                21,
                "calendar_event.updated",
                AuditEntityType.CalendarEvent
            },
            {
                AuditEventType.CalendarEventAssigneeChanged,
                22,
                "calendar_event.assignee_changed",
                AuditEntityType.CalendarEvent
            },
            {
                AuditEventType.CalendarEventDeleted,
                23,
                "calendar_event.deleted",
                AuditEntityType.CalendarEvent
            },
            {
                AuditEventType.LegalDocumentUploaded,
                24,
                "legal_document.uploaded",
                AuditEntityType.LegalDocument
            },
            {
                AuditEventType.OrganizationInvitationCreated,
                25,
                "organization_invitation.created",
                AuditEntityType.OrganizationInvitation
            },
            {
                AuditEventType.OrganizationInvitationRevoked,
                26,
                "organization_invitation.revoked",
                AuditEntityType.OrganizationInvitation
            },
            {
                AuditEventType.OrganizationInvitationAccepted,
                27,
                "organization_invitation.accepted",
                AuditEntityType.OrganizationInvitation
            },
            {
                AuditEventType.OrganizationInvitationResent,
                28,
                "organization_invitation.resent",
                AuditEntityType.OrganizationInvitation
            }
        };

    public static TheoryData<AuditEntityType, int, string> ExpectedEntities =>
        new()
        {
            { AuditEntityType.Organization, 1, "organization" },
            {
                AuditEntityType.OrganizationMembership,
                2,
                "organization_membership"
            },
            { AuditEntityType.Client, 3, "client" },
            { AuditEntityType.LegalProcess, 4, "legal_process" },
            { AuditEntityType.LegalDeadline, 5, "legal_deadline" },
            { AuditEntityType.LegalTask, 6, "legal_task" },
            { AuditEntityType.CalendarEvent, 7, "calendar_event" },
            { AuditEntityType.LegalDocument, 8, "legal_document" },
            {
                AuditEntityType.OrganizationInvitation,
                9,
                "organization_invitation"
            }
        };
}
