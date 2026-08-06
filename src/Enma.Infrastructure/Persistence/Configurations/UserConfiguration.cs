using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable(
            "users",
            tableBuilder => tableBuilder.HasCheckConstraint(
                "ck_users_email_verified_at",
                "email_verified_at IS NULL OR email_verified_at >= created_at"));

        builder.HasKey(user => user.Id)
            .HasName("pk_users");

        builder.Property(user => user.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(user => user.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .HasColumnType("character varying(150)")
            .IsRequired();

        builder.Property(user => user.Email)
            .HasColumnName("email")
            .HasMaxLength(254)
            .HasColumnType("character varying(254)")
            .IsRequired();

        builder.Property(user => user.EmailVerifiedAt)
            .HasColumnName("email_verified_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.Property(user => user.IsActive)
            .HasColumnName("is_active")
            .HasColumnType("boolean")
            .IsRequired();

        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(user => user.Email)
            .IsUnique()
            .HasDatabaseName("ux_users_email");
    }
}
