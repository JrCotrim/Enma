using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class ExternalIdentityConfiguration
    : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("external_identities");
        builder.HasKey(identity => identity.Id)
            .HasName("pk_external_identities");
        builder.Property(identity => identity.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();
        builder.Property(identity => identity.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired();
        builder.Property(identity => identity.Provider)
            .HasColumnName("provider")
            .HasColumnType("character varying(50)")
            .HasMaxLength(50)
            .IsRequired();
        builder.Property(identity => identity.ProviderSubject)
            .HasColumnName("provider_subject")
            .HasColumnType("character varying(255)")
            .HasMaxLength(255)
            .IsRequired();
        builder.Property(identity => identity.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(identity => new
            {
                identity.Provider,
                identity.ProviderSubject
            })
            .IsUnique()
            .HasDatabaseName("ux_external_identities_provider_subject");
        builder.HasIndex(identity => new { identity.UserId, identity.Provider })
            .IsUnique()
            .HasDatabaseName("ux_external_identities_user_provider");
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_external_identities_users_user_id");
    }
}
