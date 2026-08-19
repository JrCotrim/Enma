using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class LegalDocumentConfiguration
    : IEntityTypeConfiguration<LegalDocument>
{
    public void Configure(EntityTypeBuilder<LegalDocument> builder)
    {
        builder.ToTable(
            "legal_documents",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_classification",
                    "NOT (client_id IS NOT NULL AND process_id IS NOT NULL)");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_original_file_name",
                    "char_length(original_file_name) BETWEEN 1 AND 200 " +
                    "AND octet_length(original_file_name) <= 255");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_stored_object_key",
                    "stored_object_key ~ '^[0-9a-f]{32}$'");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_content_type",
                    "content_type IN (" +
                    "'application/pdf', " +
                    "'application/vnd.openxmlformats-officedocument.wordprocessingml.document', " +
                    "'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', " +
                    "'image/png', " +
                    "'image/jpeg')");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_size_bytes",
                    $"size_bytes BETWEEN 1 AND {LegalDocument.MaximumSizeBytes}");
                tableBuilder.HasCheckConstraint(
                    "ck_legal_documents_content_hash_sha256_length",
                    "octet_length(content_hash_sha256) = 32");
            });

        builder.HasKey(document => document.Id)
            .HasName("pk_legal_documents");

        builder.Property(document => document.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(document => document.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(document => document.ClientId)
            .HasColumnName("client_id")
            .HasColumnType("uuid")
            .IsRequired(false);

        builder.Property(document => document.ProcessId)
            .HasColumnName("process_id")
            .HasColumnType("uuid")
            .IsRequired(false);

        builder.Property(document => document.OriginalFileName)
            .HasColumnName("original_file_name")
            .HasMaxLength(255)
            .HasColumnType("varchar(255)")
            .IsRequired();

        builder.Property(document => document.StoredObjectKey)
            .HasColumnName("stored_object_key")
            .HasMaxLength(32)
            .HasColumnType("varchar(32)")
            .IsRequired();

        builder.Property(document => document.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(100)
            .HasColumnType("varchar(100)")
            .IsRequired();

        builder.Property(document => document.SizeBytes)
            .HasColumnName("size_bytes")
            .HasColumnType("bigint")
            .IsRequired();

        ValueConverter<LegalDocumentContentHash, byte[]>
            contentHashConverter = new(
                contentHash => contentHash.ToArray(),
                value => new LegalDocumentContentHash(value));

        ValueComparer<LegalDocumentContentHash>
            contentHashComparer = new(
                (left, right) =>
                    left == right
                    || (left != null
                        && left.Equals(right)),
                value => value.GetHashCode(),
                value => new LegalDocumentContentHash(
                    value.ToArray()));

        var contentHashProperty =
            builder.Property(
                    document => document.ContentHashSha256)
                .HasConversion(contentHashConverter)
                .HasColumnName("content_hash_sha256")
                .HasColumnType("bytea")
                .IsRequired();

        contentHashProperty.Metadata.SetValueComparer(
            contentHashComparer);

        builder.Property(
                document => document.UploadedByMembershipId)
            .HasColumnName("uploaded_by_membership_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(document => document.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(document => new
            {
                document.OrganizationId,
                document.Id
            })
            .HasName(
                "ak_legal_documents_organization_id_id");

        builder.HasIndex(document => document.StoredObjectKey)
            .IsUnique()
            .HasDatabaseName(
                "ux_legal_documents_stored_object_key");

        builder.HasIndex(document => new
            {
                document.OrganizationId,
                document.CreatedAt,
                document.Id
            })
            .IsDescending(false, true, true)
            .HasDatabaseName(
                "ix_legal_documents_organization_id_created_at_id");

        builder.HasIndex(document => new
            {
                document.OrganizationId,
                document.ClientId
            })
            .HasDatabaseName(
                "ix_legal_documents_organization_id_client_id");

        builder.HasIndex(document => new
            {
                document.OrganizationId,
                document.ProcessId
            })
            .HasDatabaseName(
                "ix_legal_documents_organization_id_process_id");

        builder.HasIndex(document => new
            {
                document.OrganizationId,
                document.UploadedByMembershipId
            })
            .HasDatabaseName(
                "ix_legal_documents_org_id_uploaded_by_membership_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(document => document.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_documents_organizations_organization_id");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(document => new
            {
                document.OrganizationId,
                document.ClientId
            })
            .HasPrincipalKey(client => new
            {
                client.OrganizationId,
                client.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_documents_clients_org_id_client_id");

        builder.HasOne<LegalProcess>()
            .WithMany()
            .HasForeignKey(document => new
            {
                document.OrganizationId,
                document.ProcessId
            })
            .HasPrincipalKey(legalProcess => new
            {
                legalProcess.OrganizationId,
                legalProcess.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_documents_processes_org_id_process_id");

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(document => new
            {
                document.OrganizationId,
                document.UploadedByMembershipId
            })
            .HasPrincipalKey(membership => new
            {
                membership.OrganizationId,
                membership.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_legal_documents_memberships_org_id_uploader_id");
    }
}
