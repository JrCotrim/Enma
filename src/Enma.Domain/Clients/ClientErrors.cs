namespace Enma.Domain.Clients;

public static class ClientErrors
{
    public const string OrganizationIdRequired = "Client organization id cannot be empty.";
    public const string NameRequired = "Client name cannot be null, empty, or whitespace.";
    public const string NameTooLong = "Client name cannot exceed 150 characters.";
    public const string EmailInvalid = "Client email must be a valid email address.";
    public const string EmailTooLong = "Client email cannot exceed 254 characters.";
    public const string PhoneInvalid = "Client phone must contain between 8 and 15 digits.";
    public const string CpfInvalid = "Client CPF must be valid.";
    public const string CreatedAtInvalid = "Client creation date must be a valid value.";
}