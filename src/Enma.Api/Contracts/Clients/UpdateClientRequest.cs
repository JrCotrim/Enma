namespace Enma.Api.Contracts.Clients;

public sealed class UpdateClientRequest
{
    public required string Name { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? Cpf { get; init; }

    public required string PersonType { get; init; }

    public required string? Cnpj { get; init; }

    public required string? Address { get; init; }

    public required string? Notes { get; init; }
}