using Enma.Domain.CalendarEvents;
using Enma.Domain.Deadlines;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration
    : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable(
            "notifications",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_notifications_kind",
                    "kind IN (1, 2, 3)");
                tableBuilder.HasCheckConstraint(
                    "ck_notifications_exactly_one_source",
                    "num_nonnulls(legal_deadline_id, legal_task_id, " +
                    "calendar_event_id) = 1");
                tableBuilder.HasCheckConstraint(
                    "ck_notifications_kind_source",
                    "(kind = 1 AND legal_deadline_id IS NOT NULL) OR " +
                    "(kind = 2 AND legal_task_id IS NOT NULL) OR " +
                    "(kind = 3 AND calendar_event_id IS NOT NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_notifications_occurrence",
                    "(kind IN (1, 2) AND occurrence_date IS NOT NULL AND " +
                    "occurrence_at IS NULL) OR " +
                    "(kind = 3 AND occurrence_date IS NULL AND " +
                    "occurrence_at IS NOT NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_notifications_read_at",
                    "read_at IS NULL OR read_at >= generated_at");
            });

        builder.HasKey(notification => notification.Id)
            .HasName("pk_notifications");

        builder.Property(notification => notification.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(notification => notification.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(notification => notification.RecipientUserId)
            .HasColumnName("recipient_user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(notification => notification.Kind)
            .HasColumnName("kind")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(notification => notification.LegalDeadlineId)
            .HasColumnName("legal_deadline_id")
            .HasColumnType("uuid");

        builder.Property(notification => notification.LegalTaskId)
            .HasColumnName("legal_task_id")
            .HasColumnType("uuid");

        builder.Property(notification => notification.CalendarEventId)
            .HasColumnName("calendar_event_id")
            .HasColumnType("uuid");

        builder.Property(notification => notification.OccurrenceDate)
            .HasColumnName("occurrence_date")
            .HasColumnType("date");

        builder.Property(notification => notification.OccurrenceAt)
            .HasColumnName("occurrence_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(notification => notification.GeneratedAt)
            .HasColumnName("generated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(notification => notification.ReadAt)
            .HasColumnName("read_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(notification => new
            {
                notification.OrganizationId,
                notification.RecipientUserId
            })
            .HasDatabaseName(
                "ix_notifications_organization_id_recipient_user_id");

        builder.HasIndex(notification => new
            {
                notification.OrganizationId,
                notification.LegalDeadlineId,
                notification.RecipientUserId,
                notification.Kind,
                notification.OccurrenceDate
            })
            .IsUnique()
            .HasDatabaseName("ux_notifications_deadline_dedupe")
            .HasFilter("legal_deadline_id IS NOT NULL");

        builder.HasIndex(notification => new
            {
                notification.OrganizationId,
                notification.LegalTaskId,
                notification.RecipientUserId,
                notification.Kind,
                notification.OccurrenceDate
            })
            .IsUnique()
            .HasDatabaseName("ux_notifications_task_dedupe")
            .HasFilter("legal_task_id IS NOT NULL");

        builder.HasIndex(notification => new
            {
                notification.OrganizationId,
                notification.CalendarEventId,
                notification.RecipientUserId,
                notification.Kind,
                notification.OccurrenceAt
            })
            .IsUnique()
            .HasDatabaseName("ux_notifications_calendar_event_dedupe")
            .HasFilter("calendar_event_id IS NOT NULL");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(notification => new
            {
                notification.OrganizationId,
                notification.RecipientUserId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.UserId
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_notifications_memberships_org_recipient_user_id");

        builder.HasOne<LegalDeadline>()
            .WithMany()
            .HasForeignKey(notification => new
            {
                notification.OrganizationId,
                notification.LegalDeadlineId
            })
            .HasPrincipalKey(legalDeadline => new
            {
                legalDeadline.OrganizationId,
                legalDeadline.Id
            })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(
                "fk_notifications_deadlines_org_legal_deadline_id");

        builder.HasOne<LegalTask>()
            .WithMany()
            .HasForeignKey(notification => new
            {
                notification.OrganizationId,
                notification.LegalTaskId
            })
            .HasPrincipalKey(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.Id
            })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_notifications_tasks_org_legal_task_id");

        builder.HasOne<CalendarEvent>()
            .WithMany()
            .HasForeignKey(notification => new
            {
                notification.OrganizationId,
                notification.CalendarEventId
            })
            .HasPrincipalKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.Id
            })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(
                "fk_notifications_calendar_events_org_calendar_event_id");
    }
}
