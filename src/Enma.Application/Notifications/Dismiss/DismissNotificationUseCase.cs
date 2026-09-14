using Enma.Application.Authorization;

namespace Enma.Application.Notifications.Dismiss;

public sealed class DismissNotificationUseCase(
    OrganizationAccessAuthorization accessAuthorization,
    INotificationMutationPersistence mutationPersistence,
    TimeProvider timeProvider)
{
    public async Task<DismissNotificationResult> ExecuteAsync(
        DismissNotificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await NotificationAccessUseCaseSupport.HasAccessAsync(
                accessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken))
        {
            return DismissNotificationResult.AccessDenied;
        }

        if (command.NotificationId == Guid.Empty)
        {
            return DismissNotificationResult.NotFound;
        }

        bool found = await mutationPersistence.DismissAsync(
            command.NotificationId,
            command.OrganizationId,
            command.UserId,
            timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);

        return found
            ? DismissNotificationResult.Succeeded
            : DismissNotificationResult.NotFound;
    }
}
