namespace Enma.Application.Notifications;

public interface INotificationMutationPersistence
{
    Task<bool> MarkAsReadAsync(
        Guid notificationId,
        Guid organizationId,
        Guid recipientUserId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default);

    Task MarkAllAsReadAsync(
        Guid organizationId,
        Guid recipientUserId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default);
}
