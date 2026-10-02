using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients", table =>
        {
            table.HasCheckConstraint(
                "ck_clients_email_normalized",
                "email IS NULL OR (email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254)");

            table.HasCheckConstraint(
                "ck_clients_phone_normalized",
                "phone IS NULL OR phone ~ '^[0-9]{8,15}$'");

            table.HasCheckConstraint(
                "ck_clients_cpf_normalized",
                "cpf IS NULL OR cpf ~ '^[0-9]{11}$'");

            table.HasCheckConstraint(
                "ck_clients_person_type",
                "person_type IN (1, 2)");

            table.HasCheckConstraint(
                "ck_clients_cnpj_normalized",
                "cnpj IS NULL OR (cnpj COLLATE \"C\") ~ '^[0-9A-Z]{12}[0-9]{2}$'");

            table.HasCheckConstraint(
                "ck_clients_document_matches_person_type",
                "(person_type = 1 AND cnpj IS NULL) OR (person_type = 2 AND cpf IS NULL)");

            table.HasCheckConstraint(
                "ck_clients_address_normalized",
                "address IS NULL OR (address = btrim(address) AND length(address) > 0)");

            table.HasCheckConstraint(
                "ck_clients_notes_normalized",
                "notes IS NULL OR (notes = btrim(notes) AND length(notes) > 0)");
        });

        builder.HasKey(client => client.Id)
            .HasName("pk_clients");

        builder.Property(client => client.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(client => client.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(client => client.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .HasColumnType("varchar(150)")
            .IsRequired();

        builder.Property(client => client.Email)
            .HasColumnName("email")
            .HasMaxLength(254)
            .HasColumnType("varchar(254)");

        builder.Property(client => client.Phone)
            .HasColumnName("phone")
            .HasMaxLength(15)
            .HasColumnType("varchar(15)");

        builder.Property(client => client.Cpf)
            .HasColumnName("cpf")
            .HasMaxLength(11)
            .HasColumnType("varchar(11)");

        builder.Property(client => client.PersonType)
            .HasColumnName("person_type")
            .HasColumnType("smallint")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(client => client.Cnpj)
            .HasColumnName("cnpj")
            .HasMaxLength(14)
            .HasColumnType("varchar(14)");

        builder.Property(client => client.Address)
            .HasColumnName("address")
            .HasMaxLength(300)
            .HasColumnType("varchar(300)");

        builder.Property(client => client.Notes)
            .HasColumnName("notes")
            .HasMaxLength(2000)
            .HasColumnType("varchar(2000)");

        builder.Property(client => client.IsActive)
            .HasColumnName("is_active")
            .HasColumnType("boolean")
            .IsRequired();

        builder.Property(client => client.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(client => new
            {
                client.OrganizationId,
                client.Id
            })
            .HasName("ak_clients_organization_id_id");

        // Inactive clients keep their documents reserved within the organization.
        builder.HasIndex(client => new
            {
                client.OrganizationId,
                client.Cpf
            })
            .IsUnique()
            .HasDatabaseName("ux_clients_organization_id_cpf")
            .HasFilter("cpf IS NOT NULL");

        builder.HasIndex(client => new
            {
                client.OrganizationId,
                client.Cnpj
            })
            .IsUnique()
            .HasDatabaseName("ux_clients_organization_id_cnpj")
            .HasFilter("cnpj IS NOT NULL");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(client => client.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_clients_organizations_organization_id");
    }
}