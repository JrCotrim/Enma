namespace Enma.Application.Deadlines;

public enum LegalDeadlineReadState
{
    Pending = 0,
    Completed = 1
}

public sealed record LegalDeadlineDetailReadModel(
    Guid Id,
    string Title,
    DateOnly DueDate,
    Guid ProcessId,
    string ProcessTitle,
    string ClientName,
    LegalDeadlineReadState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record LegalDeadlineListItem(
    Guid Id,
    string Title,
    DateOnly DueDate,
    Guid ProcessId,
    string ProcessTitle,
    string ClientName,
    LegalDeadlineReadState State);
