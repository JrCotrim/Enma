using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_verification_challenges",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_at_issue = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verification_challenges", x => x.user_id);
                    table.CheckConstraint("ck_email_verification_challenges_expiration", "expires_at > created_at");
                    table.CheckConstraint("ck_email_verification_challenges_token_hash_length", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_email_verification_challenges_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_challenges_expires_at",
                table: "email_verification_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_email_verification_challenges_token_hash",
                table: "email_verification_challenges",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_verification_challenges");
        }
    }
}
