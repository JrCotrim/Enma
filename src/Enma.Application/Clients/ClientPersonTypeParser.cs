using Enma.Application.Validation;
using Enma.Domain.Clients;

namespace Enma.Application.Clients;

internal static class ClientPersonTypeParser
{
    public static PersonType Parse(string? value)
    {
        return value switch
        {
            "individual" => PersonType.Individual,
            "company" => PersonType.Company,
            _ => throw new RequestValidationException(
                "Client person type must be 'individual' or 'company'.")
        };
    }
}
