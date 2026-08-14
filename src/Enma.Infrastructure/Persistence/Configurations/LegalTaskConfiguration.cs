using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class LegalTaskConfiguration
    : IEntityTypeConfiguration<LegalTask>
{
    public void Configure(EntityTypeBuilder<LegalTask> builder)
    {
        builder.ToTable(
            "legal_tasks",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_legal_tasks_completion",
                    "completed_at IS NULL OR completed_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_tasks_description_normalized",
                    "description IS NULL OR " +
                    "(description = btrim(description) AND length(description) > 0)");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_tasks_title_normalized",
                    "title = btrim(title) AND length(title) > 0");
            });

        builder.HasKey(legalTask => legalTask.Id)
            .HasName("pk_legal_tasks");

        builder.Property(legalTask => legalTask.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(legalTask => legalTask.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalTask => legalTask.Title)
            .HasColumnName("title")
            .HasMaxLength(150)
            .HasColumnType("varchar(150)")
            .IsRequired();

        builder.Property(legalTask => legalTask.Description)
            .HasColumnName("description")
            .HasMaxLength(2_000)
            .HasColumnType("varchar(2000)");

        builder.Property(legalTask => legalTask.DueDate)
            .HasColumnName("due_date")
            .HasColumnType("date");

        builder.Property(legalTask => legalTask.ProcessId)
            .HasColumnName("process_id")
            .HasColumnType("uuid");

        builder.Property(legalTask => legalTask.AssigneeMembershipId)
            .HasColumnName("assignee_membership_id")
            .HasColumnType("uuid");

        builder.Property(legalTask => legalTask.CreatedByMembershipId)
            .HasColumnName("created_by_membership_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalTask => legalTask.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(legalTask => legalTask.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.CreatedByMembershipId
            })
            .HasDatabaseName(
                "ix_legal_tasks_organization_id_created_by_membership_id");

        builder.HasIndex(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.DueDate,
                legalTask.CreatedAt,
                legalTask.Id
            })
            .HasDatabaseName(
                "ix_legal_tasks_pending_organization_due_date_created_at_id")
            .IsDescending(false, false, true, false)
            .HasFilter("completed_at IS NULL");

        builder.HasIndex(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.CompletedAt,
                legalTask.Id
            })
            .HasDatabaseName(
                "ix_legal_tasks_completed_organization_completed_at_id")
            .IsDescending(false, true, false)
            .HasFilter("completed_at IS NOT NULL");

        builder.HasIndex(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.ProcessId,
                legalTask.DueDate,
                legalTask.CreatedAt,
                legalTask.Id
            })
            .HasDatabaseName(
                "ix_legal_tasks_pending_org_process_due_date_created_at_id")
            .IsDescending(false, false, false, true, false)
            .HasFilter("completed_at IS NULL");

        builder.HasIndex(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.AssigneeMembershipId,
                legalTask.DueDate,
                legalTask.CreatedAt,
                legalTask.Id
            })
            .HasDatabaseName(
                "ix_legal_tasks_pending_org_assignee_due_date_created_at_id")
            .IsDescending(false, false, false, true, false)
            .HasFilter("completed_at IS NULL");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(legalTask => legalTask.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_tasks_organizations_organization_id");

        builder.HasOne<LegalProcess>()
            .WithMany()
            .HasForeignKey(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.ProcessId
            })
            .HasPrincipalKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_tasks_legal_processes_organization_id_process_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.CreatedByMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_tasks_memberships_org_created_by_membership_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(legalTask => new
            {
                legalTask.OrganizationId,
                legalTask.AssigneeMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_tasks_memberships_org_assignee_membership_id");
    }
}
