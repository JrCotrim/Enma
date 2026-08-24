namespace Enma.Application.Notifications.List;

public sealed record ListNotificationsQuery(
    Guid UserId,
    Guid OrganizationId);
