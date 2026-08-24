namespace Enma.Application.Notifications.MarkRead;

public sealed record MarkNotificationAsReadCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid NotificationId);
