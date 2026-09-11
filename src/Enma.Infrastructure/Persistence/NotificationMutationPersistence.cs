using Enma.Application.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

public sealed class NotificationMutationPersistence(EnmaDbContext dbContext)
    : INotificationMutationPersistence
{
    public async Task<bool> MarkAsReadAsync(
        Guid notificationId,
        Guid organizationId,
        Guid recipientUserId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset normalizedReadAt = readAt.ToUniversalTime();
        int updatedRows = await dbContext.Notifications
            .VisibleTo(dbContext, organizationId, recipientUserId)
            .Where(notification => notification.Id == notificationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    notification => notification.ReadAt,
                    notification => notification.ReadAt ?? normalizedReadAt),
                cancellationToken);

        return updatedRows > 0;
    }

    public async Task MarkAllAsReadAsync(
        Guid organizationId,
        Guid recipientUserId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset normalizedReadAt = readAt.ToUniversalTime();
        await dbContext.Notifications
            .VisibleTo(dbContext, organizationId, recipientUserId)
            .Where(notification =>
                notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    notification => notification.ReadAt,
                    normalizedReadAt),
                cancellationToken);
    }
}
