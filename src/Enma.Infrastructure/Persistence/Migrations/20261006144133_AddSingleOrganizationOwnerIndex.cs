using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleOrganizationOwnerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fails before the constraints are created, reporting only counts.
            // A failing CREATE UNIQUE INDEX would print the duplicated
            // organization id. The ACCESS EXCLUSIVE lock (required anyway by
            // the CHECK below) is taken first and held until the migration
            // commits, so no violating row can be written after this check.
            migrationBuilder.Sql(
                """
                DO $migration$
                DECLARE
                    organizations_with_multiple_owners bigint;
                    inactive_owner_memberships bigint;
                BEGIN
                    LOCK TABLE "public"."organization_memberships"
                        IN ACCESS EXCLUSIVE MODE;

                    SELECT count(*)
                    INTO organizations_with_multiple_owners
                    FROM (
                        SELECT 1
                        FROM "public"."organization_memberships"
                        WHERE "role" = 1
                        GROUP BY "organization_id"
                        HAVING count(*) > 1
                    ) AS duplicates;

                    SELECT count(*)
                    INTO inactive_owner_memberships
                    FROM "public"."organization_memberships"
                    WHERE "role" = 1
                      AND NOT "is_active";

                    IF organizations_with_multiple_owners > 0
                        OR inactive_owner_memberships > 0 THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23000',
                            MESSAGE = format(
                                'organization_memberships violates the single active Owner invariant: '
                                    || '%s organization(s) with more than one Owner membership; '
                                    || '%s inactive Owner membership(s)',
                                organizations_with_multiple_owners,
                                inactive_owner_memberships),
                            HINT = 'Resolve the Owner memberships before applying this migration.';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_organization_memberships_single_owner",
                table: "organization_memberships",
                column: "organization_id",
                unique: true,
                filter: "role = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_owner_active",
                table: "organization_memberships",
                sql: "role <> 1 OR is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_organization_memberships_single_owner",
                table: "organization_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_owner_active",
                table: "organization_memberships");
        }
    }
}
