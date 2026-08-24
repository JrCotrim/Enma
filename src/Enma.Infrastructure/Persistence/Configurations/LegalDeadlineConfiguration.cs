using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class LegalDeadlineConfiguration
    : IEntityTypeConfiguration<LegalDeadline>
{
    public void Configure(EntityTypeBuilder<LegalDeadline> builder)
    {
        builder.ToTable(
            "legal_deadlines",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_legal_deadlines_completion",
                    "completed_at IS NULL OR completed_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_deadlines_title_normalized",
                    "title = btrim(title) AND title <> ''");
            });

        builder.HasKey(legalDeadline => legalDeadline.Id)
            .HasName("pk_legal_deadlines");

        builder.Property(legalDeadline => legalDeadline.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(legalDeadline => legalDeadline.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalDeadline => legalDeadline.ProcessId)
            .HasColumnName("process_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalDeadline => legalDeadline.Title)
            .HasColumnName("title")
            .HasMaxLength(150)
            .HasColumnType("varchar(150)")
            .IsRequired();

        builder.Property(legalDeadline => legalDeadline.DueDate)
            .HasColumnName("due_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(legalDeadline => legalDeadline.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(legalDeadline => legalDeadline.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasAlternateKey(legalDeadline => new
            {
                legalDeadline.OrganizationId,
                legalDeadline.Id
            })
            .HasName("ak_legal_deadlines_organization_id_id");

        builder.HasIndex(legalDeadline => new
            {
                legalDeadline.OrganizationId,
                legalDeadline.DueDate,
                legalDeadline.Id
            })
            .HasDatabaseName(
                "ix_legal_deadlines_organization_id_due_date_id");

        builder.HasIndex(legalDeadline => new
            {
                legalDeadline.OrganizationId,
                legalDeadline.ProcessId,
                legalDeadline.DueDate,
                legalDeadline.Id
            })
            .HasDatabaseName(
                "ix_legal_deadlines_organization_id_process_id_due_date_id");

        builder.HasIndex(legalDeadline => new
            {
                legalDeadline.DueDate,
                legalDeadline.OrganizationId,
                legalDeadline.Id
            })
            .HasDatabaseName(
                "ix_legal_deadlines_pending_due_date_organization_id_id")
            .HasFilter("completed_at IS NULL");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(legalDeadline => legalDeadline.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_deadlines_organizations_organization_id");

        builder.HasOne<LegalProcess>()
            .WithMany()
            .HasForeignKey(legalDeadline => new
            {
                legalDeadline.OrganizationId,
                legalDeadline.ProcessId
            })
            .HasPrincipalKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_deadlines_legal_processes_organization_id_process_id");
    }
}
