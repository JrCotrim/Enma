namespace Enma.Domain.CalendarEvents;

public static class CalendarEventErrors
{
    public const string OrganizationIdRequired =
        "Calendar event organization id cannot be empty.";
    public const string CreatedByMembershipIdRequired =
        "Calendar event creator membership id cannot be empty.";
    public const string ClientIdInvalid =
        "Calendar event client id cannot be empty when supplied.";
    public const string ProcessIdInvalid =
        "Calendar event process id cannot be empty when supplied.";
    public const string AssigneeMembershipIdInvalid =
        "Calendar event assignee membership id cannot be empty when supplied.";
    public const string AssociationInvalid =
        "Calendar event cannot be associated with both a client and a process.";
    public const string TitleRequired =
        "Calendar event title cannot be null, empty, or whitespace.";
    public const string TitleTooLong =
        "Calendar event title cannot exceed 150 characters.";
    public const string DescriptionTooLong =
        "Calendar event description cannot exceed 2,000 characters.";
    public const string LocationTooLong =
        "Calendar event location cannot exceed 255 characters.";
    public const string StartsAtInvalid =
        "Calendar event start must be a valid value.";
    public const string EndsAtInvalid =
        "Calendar event end must be a valid value.";
    public const string TimeRangeInvalid =
        "Calendar event end must be later than its start.";
    public const string CreatedAtInvalid =
        "Calendar event creation date must be a valid value.";
}
