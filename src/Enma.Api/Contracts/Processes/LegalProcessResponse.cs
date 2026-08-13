namespace Enma.Api.Contracts.Processes;

public sealed record LegalProcessResponse(
    Guid Id,
    string Title,
    Guid ClientId,
    string ClientName,
    DateTimeOffset CreatedAt);
