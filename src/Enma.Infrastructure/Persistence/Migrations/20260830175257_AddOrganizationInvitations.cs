using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invited_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    created_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_invitations", x => x.id);
                    table.CheckConstraint("ck_organization_invitations_acceptance_time", "accepted_at IS NULL OR (accepted_at >= token_issued_at AND accepted_at < expires_at)");
                    table.CheckConstraint("ck_organization_invitations_accepted_by_user", "(accepted_at IS NULL) = (accepted_by_user_id IS NULL)");
                    table.CheckConstraint("ck_organization_invitations_expiration", "expires_at > token_issued_at");
                    table.CheckConstraint("ck_organization_invitations_expired_at", "expired_at IS NULL OR expired_at = expires_at");
                    table.CheckConstraint("ck_organization_invitations_revocation_time", "revoked_at IS NULL OR (revoked_at >= token_issued_at AND revoked_at < expires_at)");
                    table.CheckConstraint("ck_organization_invitations_role", "role IN (2, 3)");
                    table.CheckConstraint("ck_organization_invitations_terminal_state", "num_nonnulls(accepted_at, revoked_at, expired_at) <= 1");
                    table.CheckConstraint("ck_organization_invitations_token_hash_length", "token_hash IS NULL OR octet_length(token_hash) = 32");
                    table.CheckConstraint("ck_organization_invitations_token_issued_at", "token_issued_at >= created_at");
                    table.CheckConstraint("ck_organization_invitations_token_state", "(num_nonnulls(accepted_at, revoked_at, expired_at) = 0 AND token_hash IS NOT NULL) OR (num_nonnulls(accepted_at, revoked_at, expired_at) >= 1 AND token_hash IS NULL)");
                    table.ForeignKey(
                        name: "fk_organization_invitations_memberships_org_created_by_id",
                        columns: x => new { x.organization_id, x.created_by_membership_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_invitations_users_accepted_by_user_id",
                        column: x => x.accepted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organization_invitations_accepted_by_user_id",
                table: "organization_invitations",
                column: "accepted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organization_invitations_org_created_by_membership_id",
                table: "organization_invitations",
                columns: new[] { "organization_id", "created_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_invitations_organization_id_created_at_id",
                table: "organization_invitations",
                columns: new[] { "organization_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_organization_invitations_open_organization_id_email",
                table: "organization_invitations",
                columns: new[] { "organization_id", "invited_email" },
                unique: true,
                filter: "accepted_at IS NULL AND revoked_at IS NULL AND expired_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_organization_invitations_token_hash",
                table: "organization_invitations",
                column: "token_hash",
                unique: true,
                filter: "token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_invitations");
        }
    }
}
