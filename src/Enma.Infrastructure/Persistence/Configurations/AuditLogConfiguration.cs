using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    private const string EventsWithDetails = "1, 2, 12, 16, 17, 21, 22, 25";

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable(
            "audit_logs",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_actor_role_at_occurrence",
                    "actor_role_at_occurrence IN (1, 2, 3)");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_event_type",
                    "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, " +
                    "13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, " +
                    "25, 26, 27, 28)");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_entity_type",
                    "entity_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_event_entity_type",
                    "(event_type = 1 AND entity_type = 1) OR " +
                    "(event_type IN (2, 3, 4) AND entity_type = 2) OR " +
                    "(event_type IN (5, 6, 7, 8) AND entity_type = 3) OR " +
                    "(event_type IN (9, 10) AND entity_type = 4) OR " +
                    "(event_type IN (11, 12, 13, 14) AND entity_type = 5) OR " +
                    "(event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR " +
                    "(event_type IN (20, 21, 22, 23) AND entity_type = 7) OR " +
                    "(event_type = 24 AND entity_type = 8) OR " +
                    "(event_type IN (25, 26, 27, 28) AND entity_type = 9)");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_details_contract",
                    $"(event_type IN ({EventsWithDetails}) AND " +
                    "details IS NOT NULL AND jsonb_typeof(details) = 'object') OR " +
                    $"(event_type NOT IN ({EventsWithDetails}) AND details IS NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_details_size",
                    "details IS NULL OR " +
                    "octet_length(convert_to(details::text, 'UTF8')) <= 8192");
                tableBuilder.HasCheckConstraint(
                    "ck_audit_logs_trace_id",
                    "trace_id IS NULL OR " +
                    "(trace_id ~ '^[0-9a-f]{32}$' AND " +
                    "trace_id <> repeat('0', 32))");
            });

        builder.HasKey(auditLog => auditLog.Id)
            .HasName("pk_audit_logs");

        builder.Property(auditLog => auditLog.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(auditLog => auditLog.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(auditLog => auditLog.ActorUserId)
            .HasColumnName("actor_user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(auditLog => auditLog.ActorMembershipId)
            .HasColumnName("actor_membership_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(auditLog => auditLog.ActorRoleAtOccurrence)
            .HasColumnName("actor_role_at_occurrence")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(auditLog => auditLog.EventType)
            .HasColumnName("event_type")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(auditLog => auditLog.EntityType)
            .HasColumnName("entity_type")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(auditLog => auditLog.EntityId)
            .HasColumnName("entity_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(auditLog => auditLog.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Ignore(auditLog => auditLog.Details);
        builder.Property<string?>("_detailsJson")
            .HasColumnName("details")
            .HasColumnType("jsonb");

        builder.Property(auditLog => auditLog.TraceId)
            .HasColumnName("trace_id")
            .HasMaxLength(32)
            .HasColumnType("character varying(32)");

        builder.HasIndex(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.OccurredAt,
                auditLog.Id
            })
            .IsDescending(false, true, true)
            .HasDatabaseName(
                "ix_audit_logs_organization_id_occurred_at_id");

        builder.HasIndex(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.EntityType,
                auditLog.EntityId,
                auditLog.OccurredAt,
                auditLog.Id
            })
            .IsDescending(false, false, false, true, true)
            .HasDatabaseName(
                "ix_audit_logs_org_entity_type_entity_id_occurred_at_id");

        builder.HasIndex(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.ActorUserId,
                auditLog.OccurredAt,
                auditLog.Id
            })
            .IsDescending(false, false, true, true)
            .HasDatabaseName(
                "ix_audit_logs_org_actor_user_id_occurred_at_id");

        builder.HasIndex(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.ActorMembershipId,
                auditLog.ActorUserId
            })
            .HasDatabaseName(
                "ix_audit_logs_org_actor_membership_id_actor_user_id");

        builder.HasIndex(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.EventType,
                auditLog.OccurredAt,
                auditLog.Id
            })
            .IsDescending(false, false, true, true)
            .HasDatabaseName(
                "ix_audit_logs_org_event_type_occurred_at_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(auditLog => auditLog.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_audit_logs_organizations_organization_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(auditLog => new
            {
                auditLog.OrganizationId,
                auditLog.ActorMembershipId,
                auditLog.ActorUserId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id,
                membership.UserId
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_audit_logs_memberships_org_membership_user_id");
    }
}
