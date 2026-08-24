using Enma.Application.Authorization;

namespace Enma.Application.Notifications.List;

public sealed class ListNotificationsUseCase
{
    public const int MaximumItems = 20;

    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly INotificationReadQueries _readQueries;

    public ListNotificationsUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        INotificationReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);
        _accessAuthorization = accessAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListNotificationsResult> ExecuteAsync(
        ListNotificationsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await NotificationAccessUseCaseSupport.HasAccessAsync(
                _accessAuthorization,
                query.UserId,
                query.OrganizationId,
                cancellationToken))
        {
            return ListNotificationsResult.AccessDenied;
        }

        NotificationFeedReadResult feed = await _readQueries.ReadFeedAsync(
            query.OrganizationId,
            query.UserId,
            MaximumItems,
            cancellationToken);

        return ListNotificationsResult.Succeeded(
            feed.Items,
            feed.UnreadCount);
    }
}
