namespace Enma.Api.Contracts.Clients;

public sealed class CreateClientRequest
{
    public required string Name { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? Cpf { get; init; }

    public string? PersonType { get; init; }

    public string? Cnpj { get; init; }

    public string? Address { get; init; }

    public string? Notes { get; init; }
}