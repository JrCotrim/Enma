using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Auditing;

public sealed record ListAuditLogsResponse(
    IReadOnlyList<AuditLogResponse> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record AuditLogResponse(
    Guid Id,
    Guid ActorMembershipId,
    string ActorRoleAtOccurrence,
    string EventType,
    string EntityType,
    Guid EntityId,
    DateTimeOffset OccurredAt,
    AuditLogDetailsResponse? Details);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(
    typeof(OrganizationRenamedAuditLogDetailsResponse),
    "organization.renamed")]
[JsonDerivedType(
    typeof(OrganizationMembershipRoleChangedAuditLogDetailsResponse),
    "organization_membership.role_changed")]
[JsonDerivedType(
    typeof(OrganizationInvitationCreatedAuditLogDetailsResponse),
    "organization_invitation.created")]
[JsonDerivedType(
    typeof(LegalDeadlineDetailsChangedAuditLogDetailsResponse),
    "legal_deadline.details_changed")]
[JsonDerivedType(
    typeof(LegalTaskDetailsChangedAuditLogDetailsResponse),
    "legal_task.details_changed")]
[JsonDerivedType(
    typeof(LegalTaskAssigneeChangedAuditLogDetailsResponse),
    "legal_task.assignee_changed")]
[JsonDerivedType(
    typeof(CalendarEventUpdatedAuditLogDetailsResponse),
    "calendar_event.updated")]
[JsonDerivedType(
    typeof(CalendarEventAssigneeChangedAuditLogDetailsResponse),
    "calendar_event.assignee_changed")]
public abstract record AuditLogDetailsResponse;

public sealed record OrganizationRenamedAuditLogDetailsResponse(
    string OldName,
    string NewName) : AuditLogDetailsResponse;

public sealed record OrganizationMembershipRoleChangedAuditLogDetailsResponse(
    string OldRole,
    string NewRole) : AuditLogDetailsResponse;

public sealed record OrganizationInvitationCreatedAuditLogDetailsResponse(
    string Role) : AuditLogDetailsResponse;

public sealed record LegalDeadlineDetailsChangedAuditLogDetailsResponse(
    IReadOnlyList<string> ChangedFields) : AuditLogDetailsResponse;

public sealed record LegalTaskDetailsChangedAuditLogDetailsResponse(
    IReadOnlyList<string> ChangedFields) : AuditLogDetailsResponse;

public sealed record LegalTaskAssigneeChangedAuditLogDetailsResponse(
    Guid? OldAssigneeMembershipId,
    Guid? NewAssigneeMembershipId) : AuditLogDetailsResponse;

public sealed record CalendarEventUpdatedAuditLogDetailsResponse(
    IReadOnlyList<string> ChangedFields) : AuditLogDetailsResponse;

public sealed record CalendarEventAssigneeChangedAuditLogDetailsResponse(
    Guid? OldAssigneeMembershipId,
    Guid? NewAssigneeMembershipId) : AuditLogDetailsResponse;
