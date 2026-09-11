using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

internal static class NotificationVisibilityQueryExtensions
{
    public static IQueryable<Notification> VisibleTo(
        this IQueryable<Notification> notifications,
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid recipientUserId)
    {
        IQueryable<Guid> financeAccess =
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            join organization in dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.OrganizationId == organizationId
                && membership.UserId == recipientUserId
                && membership.IsActive
                && user.IsActive
                && organization.IsActive
                && (membership.Role == OrganizationRole.Owner ||
                    membership.Role == OrganizationRole.Administrator)
            select membership.Id;

        return notifications.Where(notification =>
            notification.OrganizationId == organizationId &&
            notification.RecipientUserId == recipientUserId &&
            (notification.Kind != NotificationKind.PaymentInstallmentDueToday ||
                financeAccess.Any()));
    }
}
