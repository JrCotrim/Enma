namespace Enma.Application.Notifications.DismissAll;

public sealed record DismissAllNotificationsCommand(
    Guid UserId,
    Guid OrganizationId);
