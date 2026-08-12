namespace Enma.Api.Contracts.Clients;

public sealed record ClientResponse(
    Guid Id,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt);
