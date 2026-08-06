using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceMonotonicPasswordChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "public"."enforce_user_credentials_password_changed_at_monotonic"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."password_changed_at" < OLD."password_changed_at" THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'ck_user_credentials_password_changed_at_monotonic',
                            MESSAGE = 'password_changed_at cannot move backward';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "trg_user_credentials_password_changed_at_monotonic"
                BEFORE UPDATE OF "password_changed_at"
                ON "public"."user_credentials"
                FOR EACH ROW
                EXECUTE FUNCTION "public"."enforce_user_credentials_password_changed_at_monotonic"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "trg_user_credentials_password_changed_at_monotonic"
                ON "public"."user_credentials";

                DROP FUNCTION IF EXISTS "public"."enforce_user_credentials_password_changed_at_monotonic"();
                """);
        }
    }
}
