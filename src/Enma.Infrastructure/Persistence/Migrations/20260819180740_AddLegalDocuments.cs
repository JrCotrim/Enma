using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_file_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    stored_object_key = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    content_type = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_hash_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_documents", x => x.id);
                    table.UniqueConstraint("ak_legal_documents_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_legal_documents_classification", "NOT (client_id IS NOT NULL AND process_id IS NOT NULL)");
                    table.CheckConstraint("ck_legal_documents_content_hash_sha256_length", "octet_length(content_hash_sha256) = 32");
                    table.CheckConstraint("ck_legal_documents_content_type", "content_type IN ('application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'image/png', 'image/jpeg')");
                    table.CheckConstraint("ck_legal_documents_original_file_name", "char_length(original_file_name) BETWEEN 1 AND 200 AND octet_length(original_file_name) <= 255");
                    table.CheckConstraint("ck_legal_documents_size_bytes", "size_bytes BETWEEN 1 AND 26214400");
                    table.CheckConstraint("ck_legal_documents_stored_object_key", "stored_object_key ~ '^[0-9a-f]{32}$'");
                    table.ForeignKey(
                        name: "fk_legal_documents_clients_org_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_documents_memberships_org_id_uploader_id",
                        columns: x => new { x.organization_id, x.uploaded_by_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_documents_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_documents_processes_org_id_process_id",
                        columns: x => new { x.organization_id, x.process_id },
                        principalTable: "legal_processes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_documents_org_id_uploaded_by_membership_id",
                table: "legal_documents",
                columns: new[] { "organization_id", "uploaded_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_legal_documents_organization_id_client_id",
                table: "legal_documents",
                columns: new[] { "organization_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_legal_documents_organization_id_created_at_id",
                table: "legal_documents",
                columns: new[] { "organization_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_legal_documents_organization_id_process_id",
                table: "legal_documents",
                columns: new[] { "organization_id", "process_id" });

            migrationBuilder.CreateIndex(
                name: "ux_legal_documents_stored_object_key",
                table: "legal_documents",
                column: "stored_object_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_documents");
        }
    }
}
