using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientContactProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs");

            migrationBuilder.AddColumn<string>(
                name: "cpf",
                table: "clients",
                type: "varchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "clients",
                type: "varchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "clients",
                type: "varchar(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_cpf_normalized",
                table: "clients",
                sql: "cpf IS NULL OR cpf ~ '^[0-9]{11}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_email_normalized",
                table: "clients",
                sql: "email IS NULL OR (email = lower(btrim(email)) AND length(email) BETWEEN 3 AND 254)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_phone_normalized",
                table: "clients",
                sql: "phone IS NULL OR phone ~ '^[0-9]{8,15}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs",
                sql: "(event_type = 1 AND entity_type = 1) OR (event_type IN (2, 3, 4) AND entity_type = 2) OR (event_type IN (5, 6, 7, 8, 29) AND entity_type = 3) OR (event_type IN (9, 10) AND entity_type = 4) OR (event_type IN (11, 12, 13, 14) AND entity_type = 5) OR (event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR (event_type IN (20, 21, 22, 23) AND entity_type = 7) OR (event_type = 24 AND entity_type = 8) OR (event_type IN (25, 26, 27, 28) AND entity_type = 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs",
                sql: "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_cpf_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_email_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_phone_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "cpf",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "email",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "clients");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_entity_type",
                table: "audit_logs",
                sql: "(event_type = 1 AND entity_type = 1) OR (event_type IN (2, 3, 4) AND entity_type = 2) OR (event_type IN (5, 6, 7, 8) AND entity_type = 3) OR (event_type IN (9, 10) AND entity_type = 4) OR (event_type IN (11, 12, 13, 14) AND entity_type = 5) OR (event_type IN (15, 16, 17, 18, 19) AND entity_type = 6) OR (event_type IN (20, 21, 22, 23) AND entity_type = 7) OR (event_type = 24 AND entity_type = 8) OR (event_type IN (25, 26, 27, 28) AND entity_type = 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_event_type",
                table: "audit_logs",
                sql: "event_type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28)");
        }
    }
}
