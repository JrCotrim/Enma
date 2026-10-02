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
    public const string CpfNotAllowedForCompany = "Company clients cannot have a CPF.";
    public const string CnpjInvalid = "Client CNPJ must be valid.";
    public const string CnpjNotAllowedForIndividual = "Individual clients cannot have a CNPJ.";
    public const string PersonTypeInvalid = "Client person type must be a valid value.";
    public const string AddressTooLong = "Client address cannot exceed 300 characters.";
    public const string NotesTooLong = "Client notes cannot exceed 2000 characters.";
    public const string CreatedAtInvalid = "Client creation date must be a valid value.";
}