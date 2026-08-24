using Enma.Application.Authorization;

namespace Enma.Application.Notifications.MarkRead;

public sealed class MarkNotificationAsReadUseCase
{
    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly INotificationMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public MarkNotificationAsReadUseCase(
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

    public async Task<MarkNotificationAsReadResult> ExecuteAsync(
        MarkNotificationAsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!await NotificationAccessUseCaseSupport.HasAccessAsync(
                _accessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken))
        {
            return MarkNotificationAsReadResult.AccessDenied;
        }

        if (command.NotificationId == Guid.Empty)
        {
            return MarkNotificationAsReadResult.NotFound;
        }

        bool found = await _mutationPersistence.MarkAsReadAsync(
            command.NotificationId,
            command.OrganizationId,
            command.UserId,
            _timeProvider.GetUtcNow().ToUniversalTime(),
            cancellationToken);

        return found
            ? MarkNotificationAsReadResult.Succeeded
            : MarkNotificationAsReadResult.NotFound;
    }
}
