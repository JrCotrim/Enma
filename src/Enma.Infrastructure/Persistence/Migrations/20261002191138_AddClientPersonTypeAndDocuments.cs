using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientPersonTypeAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every legacy client is an individual. The column is backfilled and
            // then made required so the database keeps no default value.
            migrationBuilder.AddColumn<short>(
                name: "person_type",
                table: "clients",
                type: "smallint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "public"."clients"
                SET "person_type" = 1;
                """);

            migrationBuilder.AlterColumn<short>(
                name: "person_type",
                table: "clients",
                type: "smallint",
                nullable: false,
                oldClrType: typeof(short),
                oldType: "smallint",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "clients",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cnpj",
                table: "clients",
                type: "varchar(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "clients",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_address_normalized",
                table: "clients",
                sql: "address IS NULL OR (address = btrim(address) AND length(address) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_cnpj_normalized",
                table: "clients",
                sql: "cnpj IS NULL OR (cnpj COLLATE \"C\") ~ '^[0-9A-Z]{12}[0-9]{2}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_document_matches_person_type",
                table: "clients",
                sql: "(person_type = 1 AND cnpj IS NULL) OR (person_type = 2 AND cpf IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_notes_normalized",
                table: "clients",
                sql: "notes IS NULL OR (notes = btrim(notes) AND length(notes) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_clients_person_type",
                table: "clients",
                sql: "person_type IN (1, 2)");

            // Fails before the unique indexes are created, reporting only counts.
            // A failing CREATE UNIQUE INDEX would print the duplicated document.
            // The ALTER TABLE statements above already hold an ACCESS EXCLUSIVE
            // lock on clients, so no duplicate can be written after this check.
            migrationBuilder.Sql(
                """
                DO $migration$
                DECLARE
                    duplicate_cpf_groups bigint;
                    duplicate_cpf_clients bigint;
                    duplicate_cnpj_groups bigint;
                    duplicate_cnpj_clients bigint;
                BEGIN
                    SELECT count(*), coalesce(sum(duplicates.client_count), 0)
                    INTO duplicate_cpf_groups, duplicate_cpf_clients
                    FROM (
                        SELECT count(*) AS client_count
                        FROM "public"."clients"
                        WHERE "cpf" IS NOT NULL
                        GROUP BY "organization_id", "cpf"
                        HAVING count(*) > 1
                    ) AS duplicates;

                    SELECT count(*), coalesce(sum(duplicates.client_count), 0)
                    INTO duplicate_cnpj_groups, duplicate_cnpj_clients
                    FROM (
                        SELECT count(*) AS client_count
                        FROM "public"."clients"
                        WHERE "cnpj" IS NOT NULL
                        GROUP BY "organization_id", "cnpj"
                        HAVING count(*) > 1
                    ) AS duplicates;

                    IF duplicate_cpf_groups > 0 OR duplicate_cnpj_groups > 0 THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23505',
                            MESSAGE = format(
                                'clients has duplicate documents within an organization: '
                                    || '%s CPF group(s) covering %s client(s); '
                                    || '%s CNPJ group(s) covering %s client(s)',
                                duplicate_cpf_groups,
                                duplicate_cpf_clients,
                                duplicate_cnpj_groups,
                                duplicate_cnpj_clients),
                            HINT = 'Resolve the duplicate client documents before applying this migration.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_clients_organization_id_cnpj",
                table: "clients",
                columns: new[] { "organization_id", "cnpj" },
                unique: true,
                filter: "cnpj IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_clients_organization_id_cpf",
                table: "clients",
                columns: new[] { "organization_id", "cpf" },
                unique: true,
                filter: "cpf IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data loss: dropping these columns permanently discards each client's
            // person type (companies become indistinguishable from individuals),
            // CNPJ, address, and notes. CPF values are preserved.
            migrationBuilder.DropIndex(
                name: "ux_clients_organization_id_cnpj",
                table: "clients");

            migrationBuilder.DropIndex(
                name: "ux_clients_organization_id_cpf",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_address_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_cnpj_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_document_matches_person_type",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_notes_normalized",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clients_person_type",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "address",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "cnpj",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "person_type",
                table: "clients");
        }
    }
}
