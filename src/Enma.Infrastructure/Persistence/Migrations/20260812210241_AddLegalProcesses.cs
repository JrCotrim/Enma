using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalProcesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_clients_organization_id_id",
                table: "clients",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "legal_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_legal_processes_clients_organization_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_processes_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_processes_organization_id_client_id",
                table: "legal_processes",
                columns: new[] { "organization_id", "client_id" });

            migrationBuilder.DropIndex(
                name: "ix_clients_organization_id",
                table: "clients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_processes");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_clients_organization_id_id",
                table: "clients");

            migrationBuilder.CreateIndex(
                name: "ix_clients_organization_id",
                table: "clients",
                column: "organization_id");
        }
    }
}
