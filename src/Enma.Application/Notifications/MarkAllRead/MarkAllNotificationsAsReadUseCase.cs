using Enma.Application.Authorization;

namespace Enma.Application.Notifications.MarkAllRead;

public sealed class MarkAllNotificationsAsReadUseCase
{
    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly INotificationMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public MarkAllNotificationsAsReadUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        INotificationMutationPersistence mutationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _accessAuthorization = accessAuthorization;
        _mutationPersistence = mutationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<MarkAllNotificationsAsReadResult> ExecuteAsync(
        MarkAllNotificationsAsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await NotificationAccessUseCaseSupport.HasAccessAsync(
                _accessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken))
        {
            return MarkAllNotificationsAsReadResult.AccessDenied;
        }

        await _mutationPersistence.MarkAllAsReadAsync(
            command.OrganizationId,
            command.UserId,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);

        return MarkAllNotificationsAsReadResult.Succeeded;
    }
}
