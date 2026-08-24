namespace Enma.Application.Notifications.MarkAllRead;

public sealed record MarkAllNotificationsAsReadCommand(
    Guid UserId,
    Guid OrganizationId);
