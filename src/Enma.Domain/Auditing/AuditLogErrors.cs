namespace Enma.Domain.Auditing;

public static class AuditLogErrors
{
    public const string IdRequired = "Audit log identifier is required.";
    public const string OrganizationIdRequired = "Organization is required.";
    public const string ActorUserIdRequired = "Actor user is required.";
    public const string ActorMembershipIdRequired = "Actor membership is required.";
    public const string ActorRoleInvalid = "Actor role is invalid.";
    public const string EventTypeInvalid = "Audit event type is invalid.";
    public const string EntityTypeInvalid = "Audit entity type is invalid.";
    public const string EventEntityTypeMismatch =
        "Audit event type does not match its entity type.";
    public const string EntityIdRequired = "Audit entity identifier is required.";
    public const string OccurredAtInvalid = "Audit occurrence timestamp is invalid.";
    public const string DetailsInvalidForEventType =
        "Audit details do not match the event type.";
    public const string DetailsTooLarge =
        "Audit details exceed the maximum serialized size.";
    public const string DetailsValueRequired = "Audit detail value is required.";
    public const string DetailsMustRepresentChange =
        "Audit details must represent an effective change.";
    public const string ChangedFieldsRequired =
        "At least one changed field is required.";
    public const string ChangedFieldInvalid = "Audit changed field is invalid.";
    public const string ChangedFieldDuplicate =
        "Audit changed fields must not contain duplicates.";
    public const string AssigneeMembershipIdInvalid =
        "Assignee membership identifier is invalid.";
    public const string TraceIdInvalid = "Audit trace identifier is invalid.";
}
