using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCredentialVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "credential_version",
                table: "user_credentials",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "public"."user_credentials"
                SET "credential_version" = 1;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "credential_version",
                table: "user_credentials",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_credentials_credential_version",
                table: "user_credentials",
                sql: "credential_version > 0");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION "public"."enforce_user_credentials_credential_version_monotonic"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."credential_version" > 0
                        AND NEW."credential_version" < OLD."credential_version" THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'ck_user_credentials_credential_version_monotonic',
                            MESSAGE = 'credential_version cannot move backward';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "trg_user_credentials_credential_version_monotonic"
                BEFORE UPDATE OF "credential_version"
                ON "public"."user_credentials"
                FOR EACH ROW
                EXECUTE FUNCTION "public"."enforce_user_credentials_credential_version_monotonic"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "trg_user_credentials_credential_version_monotonic"
                ON "public"."user_credentials";

                DROP FUNCTION IF EXISTS "public"."enforce_user_credentials_credential_version_monotonic"();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_credentials_credential_version",
                table: "user_credentials");

            migrationBuilder.DropColumn(
                name: "credential_version",
                table: "user_credentials");
        }
    }
}
