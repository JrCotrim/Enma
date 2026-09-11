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

        IQueryable<Notification> visibleNotifications = _dbContext.Notifications
            .AsNoTracking()
            .VisibleTo(_dbContext, organizationId, recipientUserId);

        var legacyFeedQuery =
            from notification in visibleNotifications
            where notification.Kind !=
                NotificationKind.PaymentInstallmentDueToday
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
            select new
            {
                notification.Id,
                notification.Kind,
                SourceId = (notification.LegalDeadlineId ??
                    notification.LegalTaskId ??
                    notification.CalendarEventId)!.Value,
                PaymentPlanId = (Guid?)null,
                SourceTitle = legalDeadline != null
                    ? legalDeadline.Title
                    : legalTask != null
                        ? legalTask.Title
                        : calendarEvent!.Title,
                notification.OccurrenceDate,
                notification.OccurrenceAt,
                notification.GeneratedAt,
                notification.ReadAt
            };

        var financeFeedQuery =
            from notification in visibleNotifications
            where notification.Kind ==
                NotificationKind.PaymentInstallmentDueToday
            join installment in _dbContext.PaymentInstallments.AsNoTracking()
                on new
                {
                    notification.OrganizationId,
                    SourceId = notification.PaymentInstallmentId
                }
                equals new
                {
                    installment.OrganizationId,
                    SourceId = (Guid?)installment.Id
                }
            join paymentPlan in _dbContext.ClientPaymentPlans.AsNoTracking()
                on new
                {
                    installment.OrganizationId,
                    PaymentPlanId = installment.PaymentPlanId
                }
                equals new
                {
                    paymentPlan.OrganizationId,
                    PaymentPlanId = paymentPlan.Id
                }
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    paymentPlan.OrganizationId,
                    ClientId = paymentPlan.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            select new
            {
                notification.Id,
                notification.Kind,
                SourceId = installment.Id,
                PaymentPlanId = (Guid?)paymentPlan.Id,
                SourceTitle = client.Name + " — Parcela " + installment.SequenceNumber +
                    " de " + paymentPlan.InstallmentCount,
                notification.OccurrenceDate,
                notification.OccurrenceAt,
                notification.GeneratedAt,
                notification.ReadAt
            };

        IQueryable<NotificationReadModel> feedQuery = legacyFeedQuery
            .Concat(financeFeedQuery)
            .OrderByDescending(notification => notification.GeneratedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(maximumItems)
            .Select(notification => new NotificationReadModel(
                notification.Id,
                notification.Kind,
                notification.SourceId,
                notification.PaymentPlanId,
                notification.SourceTitle,
                notification.OccurrenceDate,
                notification.OccurrenceAt,
                notification.GeneratedAt,
                notification.ReadAt));

        NotificationReadModel[] items = await feedQuery.ToArrayAsync(
            cancellationToken);
        int unreadCount = await _dbContext.Notifications
            .AsNoTracking()
            .VisibleTo(_dbContext, organizationId, recipientUserId)
            .CountAsync(
                notification => notification.ReadAt == null,
                cancellationToken);

        return new NotificationFeedReadResult(items, unreadCount);
    }
}
