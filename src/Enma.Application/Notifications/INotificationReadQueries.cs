namespace Enma.Application.Notifications;

public interface INotificationReadQueries
{
    Task<NotificationFeedReadResult> ReadFeedAsync(
        Guid organizationId,
        Guid recipientUserId,
        int maximumItems,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationFeedReadResult(
    IReadOnlyList<NotificationReadModel> Items,
    int UnreadCount);
