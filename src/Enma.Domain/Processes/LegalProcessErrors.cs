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
    public const string CreatedAtInvalid =
        "Legal process creation date must be a valid value.";
}
