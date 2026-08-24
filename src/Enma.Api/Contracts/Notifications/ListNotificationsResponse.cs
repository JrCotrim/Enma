namespace Enma.Api.Contracts.Notifications;

public sealed record ListNotificationsResponse(
    IReadOnlyList<NotificationResponse> Items,
    int UnreadCount);
