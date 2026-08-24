namespace Enma.Domain.Notifications;

public static class NotificationErrors
{
    public const string OrganizationIdRequired = "Organization is required.";
    public const string RecipientUserIdRequired = "Recipient user is required.";
    public const string KindInvalid = "Notification kind is invalid.";
    public const string LegalDeadlineIdInvalid = "Legal deadline identifier is invalid.";
    public const string LegalTaskIdInvalid = "Legal task identifier is invalid.";
    public const string CalendarEventIdInvalid = "Calendar event identifier is invalid.";
    public const string SourceInvalid = "Exactly one notification source is required.";
    public const string KindSourceMismatch = "Notification kind does not match its source.";
    public const string OccurrenceDateRequired = "Occurrence date is required for this notification kind.";
    public const string OccurrenceDateMustBeNull = "Occurrence date must be null for this notification kind.";
    public const string OccurrenceDateInvalid = "Occurrence date is invalid.";
    public const string OccurrenceAtRequired = "Occurrence instant is required for this notification kind.";
    public const string OccurrenceAtMustBeNull = "Occurrence instant must be null for this notification kind.";
    public const string OccurrenceAtInvalid = "Occurrence instant is invalid.";
    public const string GeneratedAtInvalid = "Generated timestamp is invalid.";
    public const string ReadAtInvalid = "Read timestamp must not precede generation.";
}
