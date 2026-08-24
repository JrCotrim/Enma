using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class CalendarEventConfiguration
    : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.ToTable(
            "calendar_events",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_calendar_events_association",
                    "NOT (client_id IS NOT NULL AND process_id IS NOT NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_calendar_events_description_normalized",
                    "description IS NULL OR " +
                    "(description = btrim(description) AND length(description) > 0)");
                tableBuilder.HasCheckConstraint(
                    "ck_calendar_events_location_normalized",
                    "location IS NULL OR " +
                    "(location = btrim(location) AND length(location) > 0)");
                tableBuilder.HasCheckConstraint(
                    "ck_calendar_events_time_range",
                    "ends_at > starts_at");
                tableBuilder.HasCheckConstraint(
                    "ck_calendar_events_title_normalized",
                    "title = btrim(title) AND length(title) > 0");
            });

        builder.HasKey(calendarEvent => calendarEvent.Id)
            .HasName("pk_calendar_events");

        builder.Property(calendarEvent => calendarEvent.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(calendarEvent => calendarEvent.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.Title)
            .HasColumnName("title")
            .HasMaxLength(150)
            .HasColumnType("varchar(150)")
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.Description)
            .HasColumnName("description")
            .HasMaxLength(2_000)
            .HasColumnType("varchar(2000)");

        builder.Property(calendarEvent => calendarEvent.StartsAt)
            .HasColumnName("starts_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.EndsAt)
            .HasColumnName("ends_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.Location)
            .HasColumnName("location")
            .HasMaxLength(255)
            .HasColumnType("varchar(255)");

        builder.Property(calendarEvent => calendarEvent.ClientId)
            .HasColumnName("client_id")
            .HasColumnType("uuid");

        builder.Property(calendarEvent => calendarEvent.ProcessId)
            .HasColumnName("process_id")
            .HasColumnType("uuid");

        builder.Property(calendarEvent => calendarEvent.AssigneeMembershipId)
            .HasColumnName("assignee_membership_id")
            .HasColumnType("uuid");

        builder.Property(calendarEvent => calendarEvent.CreatedByMembershipId)
            .HasColumnName("created_by_membership_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.Id
            })
            .HasName("ak_calendar_events_organization_id_id");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.StartsAt,
                calendarEvent.Id
            })
            .HasDatabaseName(
                "ix_calendar_events_organization_id_starts_at_id");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.AssigneeMembershipId,
                calendarEvent.StartsAt,
                calendarEvent.Id
            })
            .HasDatabaseName(
                "ix_calendar_events_org_assignee_starts_at_id")
            .HasFilter("assignee_membership_id IS NOT NULL");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.ProcessId,
                calendarEvent.StartsAt,
                calendarEvent.Id
            })
            .HasDatabaseName(
                "ix_calendar_events_org_process_starts_at_id")
            .HasFilter("process_id IS NOT NULL");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.ClientId,
                calendarEvent.StartsAt,
                calendarEvent.Id
            })
            .HasDatabaseName(
                "ix_calendar_events_org_client_starts_at_id")
            .HasFilter("client_id IS NOT NULL");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.CreatedByMembershipId
            })
            .HasDatabaseName(
                "ix_calendar_events_org_created_by_membership_id");

        builder.HasIndex(calendarEvent => new
            {
                calendarEvent.StartsAt,
                calendarEvent.OrganizationId,
                calendarEvent.Id
            })
            .HasDatabaseName(
                "ix_calendar_events_starts_at_organization_id_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(calendarEvent => calendarEvent.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_calendar_events_organizations_organization_id");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.ClientId
            })
            .HasPrincipalKey(client => new
            {
                client.OrganizationId,
                client.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_calendar_events_clients_organization_id_client_id");

        builder.HasOne<LegalProcess>()
            .WithMany()
            .HasForeignKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.ProcessId
            })
            .HasPrincipalKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_calendar_events_processes_organization_id_process_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.AssigneeMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_calendar_events_memberships_org_assignee_membership_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(calendarEvent => new
            {
                calendarEvent.OrganizationId,
                calendarEvent.CreatedByMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_calendar_events_memberships_org_created_by_membership_id");
    }
}
