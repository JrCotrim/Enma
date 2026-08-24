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
            .Where(notification =>
                notification.Id == notificationId &&
                notification.OrganizationId == organizationId &&
                notification.RecipientUserId == recipientUserId &&
                notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    notification => notification.ReadAt,
                    normalizedReadAt),
                cancellationToken);

        if (updatedRows > 0)
        {
            return true;
        }

        return await dbContext.Notifications
            .AsNoTracking()
            .AnyAsync(
                notification =>
                    notification.Id == notificationId &&
                    notification.OrganizationId == organizationId &&
                    notification.RecipientUserId == recipientUserId,
                cancellationToken);
    }

    public async Task MarkAllAsReadAsync(
        Guid organizationId,
        Guid recipientUserId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset normalizedReadAt = readAt.ToUniversalTime();
        await dbContext.Notifications
            .Where(notification =>
                notification.OrganizationId == organizationId &&
                notification.RecipientUserId == recipientUserId &&
                notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    notification => notification.ReadAt,
                    normalizedReadAt),
                cancellationToken);
    }
}
