namespace Enma.Api.Contracts.Clients;

public sealed record ClientResponse(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Cpf,
    bool IsActive,
    DateTimeOffset CreatedAt,
    ClientPersonTypeResponse PersonType,
    string? Cnpj,
    string? Address,
    string? Notes);