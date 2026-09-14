namespace Enma.Application.Notifications.Dismiss;

public sealed record DismissNotificationCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid NotificationId);
