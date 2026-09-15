using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class PasswordRecoveryChallengeConfiguration
    : IEntityTypeConfiguration<PasswordRecoveryChallenge>
{
    public void Configure(EntityTypeBuilder<PasswordRecoveryChallenge> builder)
    {
        builder.ToTable(
            "password_recovery_challenges",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_password_recovery_challenges_token_hash_length",
                    "octet_length(token_hash) = 32");
                tableBuilder.HasCheckConstraint(
                    "ck_password_recovery_challenges_expiration",
                    "expires_at > created_at");
            });

        builder.HasKey(challenge => challenge.UserId)
            .HasName("pk_password_recovery_challenges");

        builder.Property(challenge => challenge.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(challenge => challenge.EmailAtIssue)
            .HasColumnName("email_at_issue")
            .HasColumnType("character varying(254)")
            .HasMaxLength(254)
            .IsRequired();

        ValueConverter<PasswordRecoveryTokenHash, byte[]> converter = new(
            hash => hash.ToArray(),
            bytes => new PasswordRecoveryTokenHash(bytes));
        ValueComparer<PasswordRecoveryTokenHash> comparer = new(
            (left, right) => left == right || (left != null && left.Equals(right)),
            hash => hash.GetHashCode(),
            hash => new PasswordRecoveryTokenHash(hash.ToArray()));

        var tokenHashProperty = builder.Property(challenge => challenge.TokenHash)
            .HasConversion(converter)
            .HasColumnName("token_hash")
            .HasColumnType("bytea")
            .IsRequired();
        tokenHashProperty.Metadata.SetValueComparer(comparer);

        builder.Property(challenge => challenge.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(challenge => challenge.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(challenge => challenge.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_password_recovery_challenges_token_hash");

        builder.HasIndex(challenge => challenge.ExpiresAt)
            .HasDatabaseName("ix_password_recovery_challenges_expires_at");

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<PasswordRecoveryChallenge>(challenge => challenge.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_password_recovery_challenges_users_user_id");
    }
}
