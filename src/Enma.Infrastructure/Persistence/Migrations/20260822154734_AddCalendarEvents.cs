using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    location = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_events", x => x.id);
                    table.CheckConstraint("ck_calendar_events_association", "NOT (client_id IS NOT NULL AND process_id IS NOT NULL)");
                    table.CheckConstraint("ck_calendar_events_description_normalized", "description IS NULL OR (description = btrim(description) AND length(description) > 0)");
                    table.CheckConstraint("ck_calendar_events_location_normalized", "location IS NULL OR (location = btrim(location) AND length(location) > 0)");
                    table.CheckConstraint("ck_calendar_events_time_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_calendar_events_title_normalized", "title = btrim(title) AND length(title) > 0");
                    table.ForeignKey(
                        name: "fk_calendar_events_clients_organization_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calendar_events_memberships_org_assignee_membership_id",
                        columns: x => new { x.organization_id, x.assignee_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calendar_events_memberships_org_created_by_membership_id",
                        columns: x => new { x.organization_id, x.created_by_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calendar_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calendar_events_processes_organization_id_process_id",
                        columns: x => new { x.organization_id, x.process_id },
                        principalTable: "legal_processes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_org_assignee_starts_at_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "assignee_membership_id", "starts_at", "id" },
                filter: "assignee_membership_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_org_client_starts_at_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "client_id", "starts_at", "id" },
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_org_created_by_membership_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "created_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_org_process_starts_at_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "process_id", "starts_at", "id" },
                filter: "process_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_organization_id_starts_at_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "starts_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_events");
        }
    }
}
