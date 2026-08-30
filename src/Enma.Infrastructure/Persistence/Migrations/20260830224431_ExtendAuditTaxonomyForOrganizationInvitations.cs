using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendAuditTaxonomyForOrganizationInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_details_contract",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_details_contract",
                table: "audit_logs",
                sql: "(event_type IN (1, 2, 12, 16, 17, 21, 22, 25) AND details IS NOT NULL AND jsonb_typeof(details) = 'object') OR (event_type NOT IN (1, 2, 12, 16, 17, 21, 22, 25) AND details IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs",
                sql: "entity_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs",
                sql: "(event_type = 1 AND entity_type = 1) OR (event_type IN (2, 3, 4) AND entity_type = 2) OR (event_type IN (5, 6, 7, 8) AND entity_type = 3) OR (event_type IN (9, 10) AND entity_type = 4) OR (event_type IN (11, 12, 13, 14) AND entity_type = 5) OR (event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR (event_type IN (20, 21, 22, 23) AND entity_type = 7) OR (event_type = 24 AND entity_type = 8) OR (event_type IN (25, 26, 27, 28) AND entity_type = 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs",
                sql: "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_details_contract",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_details_contract",
                table: "audit_logs",
                sql: "(event_type IN (1, 2, 12, 16, 17, 21, 22) AND details IS NOT NULL AND jsonb_typeof(details) = 'object') OR (event_type NOT IN (1, 2, 12, 16, 17, 21, 22) AND details IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_entity_type",
                table: "audit_logs",
                sql: "entity_type IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs",
                sql: "(event_type = 1 AND entity_type = 1) OR (event_type IN (2, 3, 4) AND entity_type = 2) OR (event_type IN (5, 6, 7, 8) AND entity_type = 3) OR (event_type IN (9, 10) AND entity_type = 4) OR (event_type IN (11, 12, 13, 14) AND entity_type = 5) OR (event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR (event_type IN (20, 21, 22, 23) AND entity_type = 7) OR (event_type = 24 AND entity_type = 8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs",
                sql: "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24)");
        }
    }
}
