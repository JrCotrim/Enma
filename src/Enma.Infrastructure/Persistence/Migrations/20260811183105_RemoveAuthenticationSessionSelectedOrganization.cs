using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAuthenticationSessionSelectedOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_authentication_sessions_organizations_selected_org_id",
                table: "authentication_sessions");

            migrationBuilder.DropIndex(
                name: "ix_authentication_sessions_selected_organization_id",
                table: "authentication_sessions");

            migrationBuilder.DropColumn(
                name: "selected_organization_id",
                table: "authentication_sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "selected_organization_id",
                table: "authentication_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_authentication_sessions_selected_organization_id",
                table: "authentication_sessions",
                column: "selected_organization_id");

            migrationBuilder.AddForeignKey(
                name: "fk_authentication_sessions_organizations_selected_org_id",
                table: "authentication_sessions",
                column: "selected_organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
