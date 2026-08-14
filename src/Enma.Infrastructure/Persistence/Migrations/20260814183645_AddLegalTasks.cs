using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    process_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_tasks", x => x.id);
                    table.CheckConstraint("ck_legal_tasks_completion", "completed_at IS NULL OR completed_at >= created_at");
                    table.CheckConstraint("ck_legal_tasks_description_normalized", "description IS NULL OR (description = btrim(description) AND length(description) > 0)");
                    table.CheckConstraint("ck_legal_tasks_title_normalized", "title = btrim(title) AND length(title) > 0");
                    table.ForeignKey(
                        name: "fk_legal_tasks_legal_processes_organization_id_process_id",
                        columns: x => new { x.organization_id, x.process_id },
                        principalTable: "legal_processes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_tasks_memberships_org_assignee_membership_id",
                        columns: x => new { x.organization_id, x.assignee_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_tasks_memberships_org_created_by_membership_id",
                        columns: x => new { x.organization_id, x.created_by_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_legal_tasks_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_completed_organization_completed_at_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "completed_at", "id" },
                descending: new[] { false, true, false },
                filter: "completed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_organization_id_created_by_membership_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "created_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_pending_org_assignee_due_date_created_at_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "assignee_membership_id", "due_date", "created_at", "id" },
                descending: new[] { false, false, false, true, false },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_pending_org_process_due_date_created_at_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "process_id", "due_date", "created_at", "id" },
                descending: new[] { false, false, false, true, false },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_pending_organization_due_date_created_at_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "due_date", "created_at", "id" },
                descending: new[] { false, false, true, false },
                filter: "completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_tasks");
        }
    }
}
