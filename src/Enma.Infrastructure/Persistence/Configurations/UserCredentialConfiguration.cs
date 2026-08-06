using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class UserCredentialConfiguration
    : IEntityTypeConfiguration<UserCredential>
{
    public void Configure(EntityTypeBuilder<UserCredential> builder)
    {
        builder.ToTable(
            "user_credentials",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_user_credentials_password_changed_at",
                    "password_changed_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_user_credentials_credential_version",
                    "credential_version > 0");
            });

        builder.HasKey(credential => credential.UserId)
            .HasName("pk_user_credentials");

        builder.Property(credential => credential.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(credential => credential.PasswordHash)
            .HasColumnName("password_hash")
            .HasColumnType("character varying(512)")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(credential => credential.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(credential => credential.PasswordChangedAt)
            .HasColumnName("password_changed_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(credential => credential.CredentialVersion)
            .HasColumnName("credential_version")
            .HasColumnType("bigint")
            .IsRequired()
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserCredential>(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_credentials_users_user_id");
    }
}
