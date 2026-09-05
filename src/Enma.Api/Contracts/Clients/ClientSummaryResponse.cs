namespace Enma.Api.Contracts.Clients;

public sealed record ClientSummaryResponse(
    Guid Id,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt);