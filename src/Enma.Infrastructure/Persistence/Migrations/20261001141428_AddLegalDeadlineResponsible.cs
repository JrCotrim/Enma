using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDeadlineResponsible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "responsible_membership_id",
                table: "legal_deadlines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_legal_deadlines_organization_id_responsible_membership_id",
                table: "legal_deadlines",
                columns: new[] { "organization_id", "responsible_membership_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_legal_deadlines_memberships_org_responsible_membership_id",
                table: "legal_deadlines",
                columns: new[] { "organization_id", "responsible_membership_id" },
                principalTable: "organization_memberships",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_legal_deadlines_memberships_org_responsible_membership_id",
                table: "legal_deadlines");

            migrationBuilder.DropIndex(
                name: "ix_legal_deadlines_organization_id_responsible_membership_id",
                table: "legal_deadlines");

            migrationBuilder.DropColumn(
                name: "responsible_membership_id",
                table: "legal_deadlines");
        }
    }
}
