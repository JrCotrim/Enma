using Enma.Application.Authorization;

namespace Enma.Application.Notifications.DismissAll;

public sealed class DismissAllNotificationsUseCase(
    OrganizationAccessAuthorization accessAuthorization,
    INotificationMutationPersistence mutationPersistence,
    TimeProvider timeProvider)
{
    public async Task<DismissAllNotificationsResult> ExecuteAsync(
        DismissAllNotificationsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await NotificationAccessUseCaseSupport.HasAccessAsync(
                accessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken))
        {
            return DismissAllNotificationsResult.AccessDenied;
        }

        await mutationPersistence.DismissAllAsync(
            command.OrganizationId,
            command.UserId,
            timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);

        return DismissAllNotificationsResult.Succeeded;
    }
}
