using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class LegalProcessConfiguration
    : IEntityTypeConfiguration<LegalProcess>
{
    public void Configure(EntityTypeBuilder<LegalProcess> builder)
    {
        builder.ToTable(
            "legal_processes",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_legal_processes_status",
                    "status IN (1, 2, 3)");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_processes_process_number_pair",
                    "(process_number IS NULL AND normalized_process_number IS NULL) OR " +
                    "(process_number IS NOT NULL AND normalized_process_number IS NOT NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_processes_process_number_normalized",
                    "process_number IS NULL OR " +
                    "(process_number = btrim(process_number) AND " +
                    "length(process_number) > 0 AND " +
                    "normalized_process_number = btrim(normalized_process_number) AND " +
                    "length(normalized_process_number) > 0)");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_processes_court_or_authority_normalized",
                    "court_or_authority IS NULL OR " +
                    "(court_or_authority = btrim(court_or_authority) AND " +
                    "length(court_or_authority) > 0)");
            });

        builder.HasKey(legalProcess => legalProcess.Id)
            .HasName("pk_legal_processes");

        builder.Property(legalProcess => legalProcess.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(legalProcess => legalProcess.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalProcess => legalProcess.ClientId)
            .HasColumnName("client_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(legalProcess => legalProcess.Title)
            .HasColumnName("title")
            .HasMaxLength(150)
            .HasColumnType("varchar(150)")
            .IsRequired();

        builder.Property(legalProcess => legalProcess.ProcessNumber)
            .HasColumnName("process_number")
            .HasMaxLength(100)
            .HasColumnType("varchar(100)");

        builder.Property(legalProcess => legalProcess.NormalizedProcessNumber)
            .HasColumnName("normalized_process_number")
            .HasMaxLength(100)
            .HasColumnType("varchar(100)");

        builder.Property(legalProcess => legalProcess.Status)
            .HasColumnName("status")
            .HasColumnType("integer")
            .HasDefaultValue(LegalProcessStatus.InProgress)
            .IsRequired();

        builder.Property(legalProcess => legalProcess.CourtOrAuthority)
            .HasColumnName("court_or_authority")
            .HasMaxLength(200)
            .HasColumnType("varchar(200)");

        builder.Property(legalProcess => legalProcess.ResponsibleMembershipId)
            .HasColumnName("responsible_membership_id")
            .HasColumnType("uuid");

        builder.Property(legalProcess => legalProcess.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Id
            })
            .HasName("ak_legal_processes_organization_id_id");

        builder.HasIndex(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.ClientId
            })
            .HasDatabaseName("ix_legal_processes_organization_id_client_id");

        builder.HasIndex(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.NormalizedProcessNumber
            })
            .IsUnique()
            .HasDatabaseName(
                "ux_legal_processes_organization_id_normalized_process_number")
            .HasFilter("normalized_process_number IS NOT NULL");

        builder.HasIndex(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Status
            })
            .HasDatabaseName("ix_legal_processes_organization_id_status");

        builder.HasIndex(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.ResponsibleMembershipId
            })
            .HasDatabaseName(
                "ix_legal_processes_organization_id_responsible_membership_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(legalProcess => legalProcess.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_processes_organizations_organization_id");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.ClientId
            })
            .HasPrincipalKey(client => new
            {
                client.OrganizationId,
                client.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_processes_clients_organization_id_client_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.ResponsibleMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_processes_memberships_org_responsible_membership_id");
    }
}
