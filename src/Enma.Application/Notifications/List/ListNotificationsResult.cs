namespace Enma.Application.Notifications.List;

public sealed class ListNotificationsResult
{
    private ListNotificationsResult(
        ListNotificationsResultStatus status,
        IReadOnlyList<NotificationReadModel> items,
        int unreadCount)
    {
        Status = status;
        Items = items;
        UnreadCount = unreadCount;
    }

    public ListNotificationsResultStatus Status { get; }

    public IReadOnlyList<NotificationReadModel> Items { get; }

    public int UnreadCount { get; }

    public static ListNotificationsResult AccessDenied { get; } = new(
        ListNotificationsResultStatus.AccessDenied,
        Array.Empty<NotificationReadModel>(),
        0);

    public static ListNotificationsResult Succeeded(
        IReadOnlyList<NotificationReadModel> items,
        int unreadCount)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (unreadCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unreadCount));
        }

        return new ListNotificationsResult(
            ListNotificationsResultStatus.Succeeded,
            items.ToArray(),
            unreadCount);
    }
}

public enum ListNotificationsResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
