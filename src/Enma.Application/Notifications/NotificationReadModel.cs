using Enma.Domain.Notifications;

namespace Enma.Application.Notifications;

public sealed record NotificationReadModel(
    Guid Id,
    NotificationKind Kind,
    Guid SourceId,
    Guid? PaymentPlanId,
    string SourceTitle,
    DateOnly? OccurrenceDate,
    DateTimeOffset? OccurrenceAt,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? ReadAt);
