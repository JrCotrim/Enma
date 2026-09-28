namespace Enma.Domain.Processes;

public static class LegalProcessErrors
{
    public const string OrganizationIdRequired =
        "Legal process organization id cannot be empty.";
    public const string ClientIdRequired =
        "Legal process client id cannot be empty.";
    public const string TitleRequired =
        "Legal process title cannot be null, empty, or whitespace.";
    public const string TitleTooLong =
        "Legal process title cannot exceed 150 characters.";
    public const string ProcessNumberTooLong =
        "Legal process number cannot exceed 100 characters.";
    public const string CourtOrAuthorityTooLong =
        "Legal process court or authority cannot exceed 200 characters.";
    public const string StatusInvalid =
        "Legal process status is invalid.";
    public const string StatusTransitionInvalid =
        "Legal process status transition is not allowed.";
    public const string CreatedAtInvalid =
        "Legal process creation date must be a valid value.";
}
