namespace Enma.Domain.Deadlines;

public static class LegalDeadlineErrors
{
    public const string OrganizationIdRequired =
        "Legal deadline organization id cannot be empty.";
    public const string ProcessIdRequired =
        "Legal deadline process id cannot be empty.";
    public const string TitleRequired =
        "Legal deadline title cannot be null, empty, or whitespace.";
    public const string TitleTooLong =
        "Legal deadline title cannot exceed 150 characters.";
    public const string DueDateInvalid =
        "Legal deadline due date must be a valid value.";
    public const string CreatedAtInvalid =
        "Legal deadline creation date must be a valid value.";
    public const string CompletedAtInvalid =
        "Legal deadline completion date must be a valid value.";
    public const string CompletionBeforeCreation =
        "Legal deadline completion date cannot predate its creation date.";
    public const string CompletedDeadlineDetailsCannotChange =
        "Completed legal deadline details cannot be changed before reopening.";
}
