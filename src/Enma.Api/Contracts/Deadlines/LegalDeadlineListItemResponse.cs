namespace Enma.Api.Contracts.Deadlines;

public sealed record LegalDeadlineListItemResponse(
    Guid Id,
    string Title,
    DateOnly DueDate,
    Guid ProcessId,
    string ProcessTitle,
    string ClientName,
    LegalDeadlineStateResponse State);
