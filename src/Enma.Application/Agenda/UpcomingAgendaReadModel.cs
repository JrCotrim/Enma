namespace Enma.Application.Agenda;

public sealed record UpcomingAgendaReadModel(
    IReadOnlyList<UpcomingAgendaDeadlineReadModel> Deadlines,
    IReadOnlyList<UpcomingAgendaTaskReadModel> Tasks,
    IReadOnlyList<UpcomingAgendaCalendarEventReadModel> CalendarEvents);

public sealed record UpcomingAgendaDeadlineReadModel(
    Guid Id,
    string Title,
    DateOnly DueDate,
    string ClientName,
    string ProcessTitle);

public sealed record UpcomingAgendaTaskReadModel(
    Guid Id,
    string Title,
    DateOnly DueDate,
    string? ClientName,
    string? ProcessTitle,
    string? AssigneeDisplayName);

public sealed record UpcomingAgendaCalendarEventReadModel(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? ClientName,
    string? ProcessTitle,
    string? AssigneeDisplayName);
