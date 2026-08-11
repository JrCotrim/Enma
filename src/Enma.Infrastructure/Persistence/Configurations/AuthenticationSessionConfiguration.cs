using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class AuthenticationSessionConfiguration
    : IEntityTypeConfiguration<AuthenticationSession>
{
    public void Configure(EntityTypeBuilder<AuthenticationSession> builder)
    {
        builder.ToTable(
            "authentication_sessions",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_secret_hash_length",
                    "octet_length(secret_hash) = 32");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_credential_version_at_issue",
                    "credential_version_at_issue > 0");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_last_seen_at",
                    "last_seen_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_absolute_expires_at",
                    "absolute_expires_at > created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_idle_expires_at",
                    "idle_expires_at > last_seen_at AND idle_expires_at <= absolute_expires_at");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_revoked_at",
                    "revoked_at IS NULL OR revoked_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_authentication_sessions_concurrency_version",
                    "concurrency_version > 0");
            });

        builder.HasKey(session => session.Id)
            .HasName("pk_authentication_sessions");

        builder.Property(session => session.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(session => session.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired();

        ValueConverter<AuthenticationSessionSecretHash, byte[]> secretHashConverter =
            new(
                secretHash => secretHash.ToArray(),
                value => new AuthenticationSessionSecretHash(value));
        ValueComparer<AuthenticationSessionSecretHash> secretHashComparer =
            new(
                (left, right) => left == right ||
                    (left != null && left.Equals(right)),
                value => value.GetHashCode(),
                value => new AuthenticationSessionSecretHash(value.ToArray()));

        var secretHashProperty = builder.Property(session => session.SecretHash)
            .HasConversion(secretHashConverter)
            .HasColumnName("secret_hash")
            .HasColumnType("bytea")
            .IsRequired();
        secretHashProperty.Metadata.SetValueComparer(secretHashComparer);

        builder.Property(session => session.CredentialVersionAtIssue)
            .HasColumnName("credential_version_at_issue")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(session => session.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.LastSeenAt)
            .HasColumnName("last_seen_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.IdleExpiresAt)
            .HasColumnName("idle_expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.AbsoluteExpiresAt)
            .HasColumnName("absolute_expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.Property(session => session.ConcurrencyVersion)
            .HasColumnName("concurrency_version")
            .HasColumnType("bigint")
            .IsRequired()
            .IsConcurrencyToken()
            .ValueGeneratedNever();

        builder.HasIndex(session => session.SecretHash)
            .IsUnique()
            .HasDatabaseName("ux_authentication_sessions_secret_hash");

        builder.HasIndex(session => session.UserId)
            .HasDatabaseName("ix_authentication_sessions_user_id");

        builder.HasIndex(session => session.IdleExpiresAt)
            .HasDatabaseName("ix_authentication_sessions_idle_expires_at");

        builder.HasIndex(session => session.AbsoluteExpiresAt)
            .HasDatabaseName("ix_authentication_sessions_absolute_expires_at");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_authentication_sessions_users_user_id");
    }
}
