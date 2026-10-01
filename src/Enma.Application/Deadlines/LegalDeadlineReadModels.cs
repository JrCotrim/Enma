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
    DateTimeOffset? CompletedAt,
    Guid? ResponsibleMembershipId,
    string? ResponsibleDisplayName);

public sealed record LegalDeadlineListItem(
    Guid Id,
    string Title,
    DateOnly DueDate,
    Guid ProcessId,
    string ProcessTitle,
    string ClientName,
    LegalDeadlineReadState State,
    Guid? ResponsibleMembershipId,
    string? ResponsibleDisplayName);
