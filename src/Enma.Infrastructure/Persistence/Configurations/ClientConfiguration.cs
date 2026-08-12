using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients");

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

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(client => client.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_clients_organizations_organization_id");
    }
}
