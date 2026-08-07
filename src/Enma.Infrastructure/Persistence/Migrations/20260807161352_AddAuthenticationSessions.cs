using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authentication_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    credential_version_at_issue = table.Column<long>(type: "bigint", nullable: false),
                    selected_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    concurrency_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authentication_sessions", x => x.id);
                    table.CheckConstraint("ck_authentication_sessions_absolute_expires_at", "absolute_expires_at > created_at");
                    table.CheckConstraint("ck_authentication_sessions_concurrency_version", "concurrency_version > 0");
                    table.CheckConstraint("ck_authentication_sessions_credential_version_at_issue", "credential_version_at_issue > 0");
                    table.CheckConstraint("ck_authentication_sessions_idle_expires_at", "idle_expires_at > last_seen_at AND idle_expires_at <= absolute_expires_at");
                    table.CheckConstraint("ck_authentication_sessions_last_seen_at", "last_seen_at >= created_at");
                    table.CheckConstraint("ck_authentication_sessions_revoked_at", "revoked_at IS NULL OR revoked_at >= created_at");
                    table.CheckConstraint("ck_authentication_sessions_secret_hash_length", "octet_length(secret_hash) = 32");
                    table.ForeignKey(
                        name: "fk_authentication_sessions_organizations_selected_org_id",
                        column: x => x.selected_organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_authentication_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authentication_sessions_absolute_expires_at",
                table: "authentication_sessions",
                column: "absolute_expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_sessions_idle_expires_at",
                table: "authentication_sessions",
                column: "idle_expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_sessions_selected_organization_id",
                table: "authentication_sessions",
                column: "selected_organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_authentication_sessions_user_id",
                table: "authentication_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_authentication_sessions_secret_hash",
                table: "authentication_sessions",
                column: "secret_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authentication_sessions");
        }
    }
}
