namespace Enma.Domain.Organizations;

public static class OrganizationErrors
{
    public const string NameRequired = "Organization name cannot be null, empty, or whitespace.";
    public const string NameTooLong = "Organization name cannot exceed 150 characters.";
    public const string SlugRequired = "Organization slug cannot be null, empty, or whitespace.";
    public const string SlugTooLong = "Organization slug cannot exceed 80 characters.";
    public const string SlugInvalidFormat = "Organization slug must contain only lowercase letters, numbers, and single hyphens, and must start and end with a letter or number.";
    public const string CreatedAtInvalid = "Organization creation date cannot be DateTimeOffset.MinValue.";
}
