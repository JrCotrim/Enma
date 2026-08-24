using Enma.Application.Notifications;
using Enma.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class NotificationReadQueries : INotificationReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public NotificationReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<NotificationFeedReadResult> ReadFeedAsync(
        Guid organizationId,
        Guid recipientUserId,
        int maximumItems,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        IQueryable<Notification> boundedNotifications = _dbContext.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.OrganizationId == organizationId &&
                notification.RecipientUserId == recipientUserId)
            .OrderByDescending(notification => notification.GeneratedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(maximumItems);

        IQueryable<NotificationReadModel> feedQuery =
            from notification in boundedNotifications
            join legalDeadline in _dbContext.LegalDeadlines.AsNoTracking()
                on new
                {
                    notification.OrganizationId,
                    SourceId = notification.LegalDeadlineId
                }
                equals new
                {
                    legalDeadline.OrganizationId,
                    SourceId = (Guid?)legalDeadline.Id
                }
                into legalDeadlines
            from legalDeadline in legalDeadlines.DefaultIfEmpty()
            join legalTask in _dbContext.LegalTasks.AsNoTracking()
                on new
                {
                    notification.OrganizationId,
                    SourceId = notification.LegalTaskId
                }
                equals new
                {
                    legalTask.OrganizationId,
                    SourceId = (Guid?)legalTask.Id
                }
                into legalTasks
            from legalTask in legalTasks.DefaultIfEmpty()
            join calendarEvent in _dbContext.CalendarEvents.AsNoTracking()
                on new
                {
                    notification.OrganizationId,
                    SourceId = notification.CalendarEventId
                }
                equals new
                {
                    calendarEvent.OrganizationId,
                    SourceId = (Guid?)calendarEvent.Id
                }
                into calendarEvents
            from calendarEvent in calendarEvents.DefaultIfEmpty()
            orderby notification.GeneratedAt descending,
                notification.Id descending
            select new NotificationReadModel(
                notification.Id,
                notification.Kind,
                (notification.LegalDeadlineId ??
                    notification.LegalTaskId ??
                    notification.CalendarEventId)!.Value,
                legalDeadline != null
                    ? legalDeadline.Title
                    : legalTask != null
                        ? legalTask.Title
                        : calendarEvent!.Title,
                notification.OccurrenceDate,
                notification.OccurrenceAt,
                notification.GeneratedAt,
                notification.ReadAt);

        NotificationReadModel[] items = await feedQuery.ToArrayAsync(
            cancellationToken);
        int unreadCount = await _dbContext.Notifications
            .AsNoTracking()
            .CountAsync(
                notification =>
                    notification.OrganizationId == organizationId &&
                    notification.RecipientUserId == recipientUserId &&
                    notification.ReadAt == null,
                cancellationToken);

        return new NotificationFeedReadResult(items, unreadCount);
    }
}
