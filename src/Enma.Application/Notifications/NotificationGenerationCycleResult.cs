namespace Enma.Application.Notifications;

public sealed record NotificationGenerationCycleResult(
    DateTimeOffset GeneratedAt,
    NotificationGenerationSourceResult LegalDeadlines,
    NotificationGenerationSourceResult LegalTasks,
    NotificationGenerationSourceResult CalendarEvents);
