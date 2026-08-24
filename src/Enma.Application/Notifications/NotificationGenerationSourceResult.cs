namespace Enma.Application.Notifications;

public sealed record NotificationGenerationSourceResult(
    int InsertedCount,
    int BatchCount);
