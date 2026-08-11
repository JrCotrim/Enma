namespace Enma.Domain.Clients;

public static class ClientErrors
{
    public const string OrganizationIdRequired = "Client organization id cannot be empty.";
    public const string NameRequired = "Client name cannot be null, empty, or whitespace.";
    public const string NameTooLong = "Client name cannot exceed 150 characters.";
    public const string CreatedAtInvalid = "Client creation date must be a valid value.";
}
