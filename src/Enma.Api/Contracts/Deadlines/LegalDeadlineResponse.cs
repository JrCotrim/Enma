namespace Enma.Api.Contracts.Deadlines;

public sealed record LegalDeadlineResponse(
    Guid Id,
    string Title,
    DateOnly DueDate,
    Guid ProcessId,
    string ProcessTitle,
    string ClientName,
    LegalDeadlineStateResponse State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
