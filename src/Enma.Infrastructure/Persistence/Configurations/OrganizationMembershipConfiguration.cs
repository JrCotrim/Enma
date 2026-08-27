using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class OrganizationMembershipConfiguration
    : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.ToTable(
            "organization_memberships",
            tableBuilder => tableBuilder.HasCheckConstraint(
                "ck_organization_memberships_role",
                "role IN (1, 2, 3)"));

        builder.HasKey(membership => membership.Id)
            .HasName("pk_organization_memberships");

        builder.Property(membership => membership.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(membership => membership.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(membership => membership.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(membership => membership.Role)
            .HasColumnName("role")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(membership => membership.IsActive)
            .HasColumnName("is_active")
            .HasColumnType("boolean")
            .IsRequired();

        builder.Property(membership => membership.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .HasName("ak_organization_memberships_organization_id_id");

        builder.HasAlternateKey(membership => new
            {
                membership.OrganizationId,
                membership.UserId
            })
            .HasName(
                "ux_organization_memberships_organization_id_user_id");

        builder.HasAlternateKey(membership => new
            {
                membership.OrganizationId,
                membership.Id,
                membership.UserId
            })
            .HasName(
                "ak_organization_memberships_organization_id_id_user_id");

        builder.HasIndex(membership => membership.UserId)
            .HasDatabaseName("ix_organization_memberships_user_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_organization_memberships_organizations_organization_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_organization_memberships_users_user_id");
    }
}
