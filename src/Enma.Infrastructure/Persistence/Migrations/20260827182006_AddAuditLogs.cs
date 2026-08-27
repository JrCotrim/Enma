using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_organization_memberships_organization_id_id_user_id",
                table: "organization_memberships",
                columns: new[] { "organization_id", "id", "user_id" });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_role_at_occurrence = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    entity_type = table.Column<int>(type: "integer", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_actor_role_at_occurrence", "actor_role_at_occurrence IN (1, 2, 3)");
                    table.CheckConstraint("ck_audit_logs_details_contract", "(event_type IN (1, 2, 12, 16, 17, 21, 22) AND details IS NOT NULL AND jsonb_typeof(details) = 'object') OR (event_type NOT IN (1, 2, 12, 16, 17, 21, 22) AND details IS NULL)");
                    table.CheckConstraint("ck_audit_logs_details_size", "details IS NULL OR octet_length(convert_to(details::text, 'UTF8')) <= 8192");
                    table.CheckConstraint("ck_audit_logs_entity_type", "entity_type IN (1, 2, 3, 4, 5, 6, 7, 8)");
                    table.CheckConstraint("ck_audit_logs_event_entity_type", "(event_type = 1 AND entity_type = 1) OR (event_type IN (2, 3, 4) AND entity_type = 2) OR (event_type IN (5, 6, 7, 8) AND entity_type = 3) OR (event_type IN (9, 10) AND entity_type = 4) OR (event_type IN (11, 12, 13, 14) AND entity_type = 5) OR (event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR (event_type IN (20, 21, 22, 23) AND entity_type = 7) OR (event_type = 24 AND entity_type = 8)");
                    table.CheckConstraint("ck_audit_logs_event_type", "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24)");
                    table.CheckConstraint("ck_audit_logs_trace_id", "trace_id IS NULL OR (trace_id ~ '^[0-9a-f]{32}$' AND trace_id <> repeat('0', 32))");
                    table.ForeignKey(
                        name: "fk_audit_logs_memberships_org_membership_user_id",
                        columns: x => new { x.organization_id, x.actor_membership_id, x.actor_user_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_logs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_org_actor_user_id_occurred_at_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "actor_user_id", "occurred_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_org_actor_membership_id_actor_user_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "actor_membership_id", "actor_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_org_entity_type_entity_id_occurred_at_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "entity_type", "entity_id", "occurred_at", "id" },
                descending: new[] { false, false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_org_event_type_occurred_at_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "event_type", "occurred_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_id_occurred_at_id",
                table: "audit_logs",
                columns: new[] { "organization_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            // This prevents normal application and accidental SQL mutation. A table
            // owner or migration principal can still disable or drop this protection.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "public"."prevent_audit_logs_mutation"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE = 'audit_logs is append-only';
                END;
                $function$;

                CREATE TRIGGER "trg_audit_logs_append_only"
                BEFORE UPDATE OR DELETE OR TRUNCATE
                ON "public"."audit_logs"
                FOR EACH STATEMENT
                EXECUTE FUNCTION "public"."prevent_audit_logs_mutation"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "trg_audit_logs_append_only"
                ON "public"."audit_logs";

                DROP FUNCTION IF EXISTS "public"."prevent_audit_logs_mutation"();
                """);

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_organization_memberships_organization_id_id_user_id",
                table: "organization_memberships");
        }
    }
}
