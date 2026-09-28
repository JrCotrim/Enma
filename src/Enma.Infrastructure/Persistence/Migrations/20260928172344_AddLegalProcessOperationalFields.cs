using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalProcessOperationalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "court_or_authority",
                table: "legal_processes",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalized_process_number",
                table: "legal_processes",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "process_number",
                table: "legal_processes",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "responsible_membership_id",
                table: "legal_processes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "legal_processes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_legal_processes_organization_id_responsible_membership_id",
                table: "legal_processes",
                columns: new[] { "organization_id", "responsible_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_legal_processes_organization_id_status",
                table: "legal_processes",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_legal_processes_organization_id_normalized_process_number",
                table: "legal_processes",
                columns: new[] { "organization_id", "normalized_process_number" },
                unique: true,
                filter: "normalized_process_number IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_legal_processes_court_or_authority_normalized",
                table: "legal_processes",
                sql: "court_or_authority IS NULL OR (court_or_authority = btrim(court_or_authority) AND length(court_or_authority) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_legal_processes_process_number_normalized",
                table: "legal_processes",
                sql: "process_number IS NULL OR (process_number = btrim(process_number) AND length(process_number) > 0 AND normalized_process_number = btrim(normalized_process_number) AND length(normalized_process_number) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_legal_processes_process_number_pair",
                table: "legal_processes",
                sql: "(process_number IS NULL AND normalized_process_number IS NULL) OR (process_number IS NOT NULL AND normalized_process_number IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_legal_processes_status",
                table: "legal_processes",
                sql: "status IN (1, 2, 3)");

            migrationBuilder.AddForeignKey(
                name: "fk_legal_processes_memberships_org_responsible_membership_id",
                table: "legal_processes",
                columns: new[] { "organization_id", "responsible_membership_id" },
                principalTable: "organization_memberships",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_legal_processes_memberships_org_responsible_membership_id",
                table: "legal_processes");

            migrationBuilder.DropIndex(
                name: "ix_legal_processes_organization_id_responsible_membership_id",
                table: "legal_processes");

            migrationBuilder.DropIndex(
                name: "ix_legal_processes_organization_id_status",
                table: "legal_processes");

            migrationBuilder.DropIndex(
                name: "ux_legal_processes_organization_id_normalized_process_number",
                table: "legal_processes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_legal_processes_court_or_authority_normalized",
                table: "legal_processes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_legal_processes_process_number_normalized",
                table: "legal_processes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_legal_processes_process_number_pair",
                table: "legal_processes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_legal_processes_status",
                table: "legal_processes");

            migrationBuilder.DropColumn(
                name: "court_or_authority",
                table: "legal_processes");

            migrationBuilder.DropColumn(
                name: "normalized_process_number",
                table: "legal_processes");

            migrationBuilder.DropColumn(
                name: "process_number",
                table: "legal_processes");

            migrationBuilder.DropColumn(
                name: "responsible_membership_id",
                table: "legal_processes");

            migrationBuilder.DropColumn(
                name: "status",
                table: "legal_processes");
        }
    }
}
