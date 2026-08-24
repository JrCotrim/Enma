namespace Enma.Api.Contracts.Notifications;

public sealed record NotificationResponse(
    Guid Id,
    NotificationKindResponse Kind,
    NotificationSourceTypeResponse SourceType,
    Guid SourceId,
    string SourceTitle,
    DateOnly? OccurrenceDate,
    DateTimeOffset? OccurrenceAt,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? ReadAt);
