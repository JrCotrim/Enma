using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_legal_processes_organization_id_id",
                table: "legal_processes",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "legal_deadlines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_deadlines", x => x.id);
                    table.CheckConstraint("ck_legal_deadlines_completion", "completed_at IS NULL OR completed_at >= created_at");
                    table.CheckConstraint("ck_legal_deadlines_title_normalized", "title = btrim(title) AND title <> ''");
                    table.ForeignKey(
                        name: "fk_legal_deadlines_legal_processes_organization_id_process_id",
                        columns: x => new { x.organization_id, x.process_id },
                        principalTable: "legal_processes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_deadlines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_deadlines_organization_id_due_date_id",
                table: "legal_deadlines",
                columns: new[] { "organization_id", "due_date", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_legal_deadlines_organization_id_process_id_due_date_id",
                table: "legal_deadlines",
                columns: new[] { "organization_id", "process_id", "due_date", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_deadlines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_legal_processes_organization_id_id",
                table: "legal_processes");
        }
    }
}
