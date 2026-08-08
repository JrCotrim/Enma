using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class EmailVerificationChallengeConfiguration
    : IEntityTypeConfiguration<EmailVerificationChallenge>
{
    public void Configure(EntityTypeBuilder<EmailVerificationChallenge> builder)
    {
        builder.ToTable(
            "email_verification_challenges",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_challenges_token_hash_length",
                    "octet_length(token_hash) = 32");
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_challenges_expiration",
                    "expires_at > created_at");
            });

        builder.HasKey(challenge => challenge.UserId)
            .HasName("pk_email_verification_challenges");

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

        ValueConverter<EmailVerificationTokenHash, byte[]> tokenHashConverter =
            new(
                hash => hash.ToArray(),
                bytes => new EmailVerificationTokenHash(bytes));
        ValueComparer<EmailVerificationTokenHash> tokenHashComparer =
            new(
                (left, right) => left == right ||
                    (left != null && left.Equals(right)),
                hash => hash.GetHashCode(),
                hash => new EmailVerificationTokenHash(hash.ToArray()));

        var tokenHashProperty = builder.Property(challenge => challenge.TokenHash)
            .HasConversion(tokenHashConverter)
            .HasColumnName("token_hash")
            .HasColumnType("bytea")
            .IsRequired();
        tokenHashProperty.Metadata.SetValueComparer(tokenHashComparer);

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
            .HasDatabaseName("ux_email_verification_challenges_token_hash");

        builder.HasIndex(challenge => challenge.ExpiresAt)
            .HasDatabaseName("ix_email_verification_challenges_expires_at");

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<EmailVerificationChallenge>(challenge => challenge.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(
                "fk_email_verification_challenges_users_user_id");
    }
}
