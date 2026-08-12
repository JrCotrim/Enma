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
        builder.ToTable("legal_processes");

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

        builder.Property(legalProcess => legalProcess.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.ClientId
            })
            .HasDatabaseName("ix_legal_processes_organization_id_client_id");

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
    }
}
