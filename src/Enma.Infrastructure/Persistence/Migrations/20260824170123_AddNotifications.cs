using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_organization_memberships_organization_id_user_id",
                table: "organization_memberships");

            migrationBuilder.AddUniqueConstraint(
                name: "ux_organization_memberships_organization_id_user_id",
                table: "organization_memberships",
                columns: new[] { "organization_id", "user_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_legal_tasks_organization_id_id",
                table: "legal_tasks",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_legal_deadlines_organization_id_id",
                table: "legal_deadlines",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_calendar_events_organization_id_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    legal_deadline_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legal_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurrence_date = table.Column<DateOnly>(type: "date", nullable: true),
                    occurrence_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_exactly_one_source", "num_nonnulls(legal_deadline_id, legal_task_id, calendar_event_id) = 1");
                    table.CheckConstraint("ck_notifications_kind", "kind IN (1, 2, 3)");
                    table.CheckConstraint("ck_notifications_kind_source", "(kind = 1 AND legal_deadline_id IS NOT NULL) OR (kind = 2 AND legal_task_id IS NOT NULL) OR (kind = 3 AND calendar_event_id IS NOT NULL)");
                    table.CheckConstraint("ck_notifications_occurrence", "(kind IN (1, 2) AND occurrence_date IS NOT NULL AND occurrence_at IS NULL) OR (kind = 3 AND occurrence_date IS NULL AND occurrence_at IS NOT NULL)");
                    table.CheckConstraint("ck_notifications_read_at", "read_at IS NULL OR read_at >= generated_at");
                    table.ForeignKey(
                        name: "fk_notifications_calendar_events_org_calendar_event_id",
                        columns: x => new { x.organization_id, x.calendar_event_id },
                        principalTable: "calendar_events",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_deadlines_org_legal_deadline_id",
                        columns: x => new { x.organization_id, x.legal_deadline_id },
                        principalTable: "legal_deadlines",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_memberships_org_recipient_user_id",
                        columns: x => new { x.organization_id, x.recipient_user_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_tasks_org_legal_task_id",
                        columns: x => new { x.organization_id, x.legal_task_id },
                        principalTable: "legal_tasks",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_tasks_pending_due_date_organization_id_id",
                table: "legal_tasks",
                columns: new[] { "due_date", "organization_id", "id" },
                filter: "completed_at IS NULL AND due_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_legal_deadlines_pending_due_date_organization_id_id",
                table: "legal_deadlines",
                columns: new[] { "due_date", "organization_id", "id" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_starts_at_organization_id_id",
                table: "calendar_events",
                columns: new[] { "starts_at", "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_organization_id_recipient_user_id",
                table: "notifications",
                columns: new[] { "organization_id", "recipient_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_calendar_event_dedupe",
                table: "notifications",
                columns: new[] { "organization_id", "calendar_event_id", "recipient_user_id", "kind", "occurrence_at" },
                unique: true,
                filter: "calendar_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_deadline_dedupe",
                table: "notifications",
                columns: new[] { "organization_id", "legal_deadline_id", "recipient_user_id", "kind", "occurrence_date" },
                unique: true,
                filter: "legal_deadline_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_task_dedupe",
                table: "notifications",
                columns: new[] { "organization_id", "legal_task_id", "recipient_user_id", "kind", "occurrence_date" },
                unique: true,
                filter: "legal_task_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropUniqueConstraint(
                name: "ux_organization_memberships_organization_id_user_id",
                table: "organization_memberships");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_legal_tasks_organization_id_id",
                table: "legal_tasks");

            migrationBuilder.DropIndex(
                name: "ix_legal_tasks_pending_due_date_organization_id_id",
                table: "legal_tasks");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_legal_deadlines_organization_id_id",
                table: "legal_deadlines");

            migrationBuilder.DropIndex(
                name: "ix_legal_deadlines_pending_due_date_organization_id_id",
                table: "legal_deadlines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_calendar_events_organization_id_id",
                table: "calendar_events");

            migrationBuilder.DropIndex(
                name: "ix_calendar_events_starts_at_organization_id_id",
                table: "calendar_events");

            migrationBuilder.CreateIndex(
                name: "ux_organization_memberships_organization_id_user_id",
                table: "organization_memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true);
        }
    }
}
