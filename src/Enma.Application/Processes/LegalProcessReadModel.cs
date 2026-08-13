namespace Enma.Application.Processes;

public sealed record LegalProcessReadModel(
    Guid Id,
    string Title,
    Guid ClientId,
    string ClientName,
    DateTimeOffset CreatedAt);
