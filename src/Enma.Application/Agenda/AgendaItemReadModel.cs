namespace Enma.Application.Agenda;

public enum AgendaItemKind
{
    Deadline = 0,
    Task = 1,
    CalendarEvent = 2
}

public sealed record AgendaItemReadModel(
    AgendaItemKind Kind,
    Guid Id,
    string Title,
    bool IsAllDay,
    DateOnly? Date,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? CompletedAt,
    Guid? ClientId,
    string? ClientName,
    Guid? ProcessId,
    string? ProcessTitle,
    Guid? AssigneeMembershipId,
    string? AssigneeDisplayName);
