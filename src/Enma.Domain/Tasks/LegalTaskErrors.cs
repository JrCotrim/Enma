namespace Enma.Domain.Tasks;

public static class LegalTaskErrors
{
    public const string OrganizationIdRequired =
        "Legal task organization id cannot be empty.";
    public const string CreatedByMembershipIdRequired =
        "Legal task creator membership id cannot be empty.";
    public const string ProcessIdInvalid =
        "Legal task process id cannot be empty when supplied.";
    public const string AssigneeMembershipIdInvalid =
        "Legal task assignee membership id cannot be empty when supplied.";
    public const string TitleRequired =
        "Legal task title cannot be null, empty, or whitespace.";
    public const string TitleTooLong =
        "Legal task title cannot exceed 150 characters.";
    public const string DescriptionTooLong =
        "Legal task description cannot exceed 2,000 characters.";
    public const string DueDateInvalid =
        "Legal task due date must be a valid value when supplied.";
    public const string CreatedAtInvalid =
        "Legal task creation date must be a valid value.";
    public const string CompletedAtInvalid =
        "Legal task completion date must be a valid value.";
    public const string CompletionBeforeCreation =
        "Legal task completion date cannot predate its creation date.";
    public const string CompletedTaskCannotChange =
        "Completed legal task details and assignment cannot be changed before reopening.";
}
