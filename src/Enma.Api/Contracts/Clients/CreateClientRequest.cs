namespace Enma.Api.Contracts.Clients;

public sealed class CreateClientRequest
{
    public required string Name { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? Cpf { get; init; }
}