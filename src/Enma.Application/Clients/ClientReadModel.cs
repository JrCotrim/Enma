namespace Enma.Application.Clients;

public sealed record ClientReadModel(
    Guid Id,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? Email = null,
    string? Phone = null,
    string? Cpf = null);