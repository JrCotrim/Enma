using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class OrganizationInvitationConfiguration
    : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> builder)
    {
        builder.ToTable(
            "organization_invitations",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_role",
                    "role IN (2, 3)");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_terminal_state",
                    "num_nonnulls(accepted_at, revoked_at, expired_at) <= 1");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_accepted_by_user",
                    "(accepted_at IS NULL) = (accepted_by_user_id IS NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_token_state",
                    "(num_nonnulls(accepted_at, revoked_at, expired_at) = 0 " +
                    "AND token_hash IS NOT NULL) OR " +
                    "(num_nonnulls(accepted_at, revoked_at, expired_at) >= 1 " +
                    "AND token_hash IS NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_token_hash_length",
                    "token_hash IS NULL OR octet_length(token_hash) = 32");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_token_issued_at",
                    "token_issued_at >= created_at");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_expiration",
                    "expires_at > token_issued_at");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_acceptance_time",
                    "accepted_at IS NULL OR " +
                    "(accepted_at >= token_issued_at AND accepted_at < expires_at)");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_revocation_time",
                    "revoked_at IS NULL OR " +
                    "(revoked_at >= token_issued_at AND revoked_at < expires_at)");
                tableBuilder.HasCheckConstraint(
                    "ck_organization_invitations_expired_at",
                    "expired_at IS NULL OR expired_at = expires_at");
            });

        builder.HasKey(invitation => invitation.Id)
            .HasName("pk_organization_invitations");

        builder.Property(invitation => invitation.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(invitation => invitation.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(invitation => invitation.InvitedEmail)
            .HasColumnName("invited_email")
            .HasColumnType("character varying(254)")
            .HasMaxLength(254)
            .IsRequired();

        builder.Property(invitation => invitation.Role)
            .HasColumnName("role")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(invitation => invitation.CreatedByMembershipId)
            .HasColumnName("created_by_membership_id")
            .HasColumnType("uuid")
            .IsRequired();

        ValueConverter<OrganizationInvitationTokenHash?, byte[]?> tokenHashConverter =
            new(
                hash => hash == null ? null : hash.ToArray(),
                bytes => bytes == null
                    ? null
                    : new OrganizationInvitationTokenHash(bytes));
        ValueComparer<OrganizationInvitationTokenHash?> tokenHashComparer =
            new(
                (left, right) => left == right ||
                    (left != null && left.Equals(right)),
                hash => hash == null ? 0 : hash.GetHashCode(),
                hash => hash == null
                    ? null
                    : new OrganizationInvitationTokenHash(hash.ToArray()));

        var tokenHashProperty = builder.Property(invitation => invitation.TokenHash)
            .HasConversion(tokenHashConverter)
            .HasColumnName("token_hash")
            .HasColumnType("bytea")
            .IsRequired(false);
        tokenHashProperty.Metadata.SetValueComparer(tokenHashComparer);

        builder.Property(invitation => invitation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(invitation => invitation.TokenIssuedAt)
            .HasColumnName("token_issued_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(invitation => invitation.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(invitation => invitation.AcceptedAt)
            .HasColumnName("accepted_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.Property(invitation => invitation.AcceptedByUserId)
            .HasColumnName("accepted_by_user_id")
            .HasColumnType("uuid")
            .IsRequired(false);

        builder.Property(invitation => invitation.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.Property(invitation => invitation.ExpiredAt)
            .HasColumnName("expired_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.HasIndex(invitation => invitation.TokenHash)
            .IsUnique()
            .HasFilter("token_hash IS NOT NULL")
            .HasDatabaseName("ux_organization_invitations_token_hash");

        builder.HasIndex(invitation => new
            {
                invitation.OrganizationId,
                invitation.InvitedEmail
            })
            .IsUnique()
            .HasFilter(
                "accepted_at IS NULL AND revoked_at IS NULL AND expired_at IS NULL")
            .HasDatabaseName(
                "ux_organization_invitations_open_organization_id_email");

        builder.HasIndex(invitation => new
            {
                invitation.OrganizationId,
                invitation.CreatedAt,
                invitation.Id
            })
            .IsDescending(false, true, true)
            .HasDatabaseName(
                "ix_organization_invitations_organization_id_created_at_id");

        builder.HasIndex(invitation => invitation.AcceptedByUserId)
            .HasDatabaseName(
                "ix_organization_invitations_accepted_by_user_id");

        builder.HasIndex(invitation => new
            {
                invitation.OrganizationId,
                invitation.CreatedByMembershipId
            })
            .HasDatabaseName(
                "ix_organization_invitations_org_created_by_membership_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(invitation => invitation.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_organization_invitations_organizations_organization_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(invitation => new
            {
                invitation.OrganizationId,
                invitation.CreatedByMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_organization_invitations_memberships_org_created_by_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(invitation => invitation.AcceptedByUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_organization_invitations_users_accepted_by_user_id");
    }
}
