using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationSendBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_verification_send_budgets",
                columns: table => new
                {
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verification_send_budgets", x => new { x.scope, x.key_hash });
                    table.CheckConstraint("ck_email_verification_send_budgets_key_hash_length", "octet_length(key_hash) = 32");
                    table.CheckConstraint("ck_email_verification_send_budgets_scope", "scope IN (1, 2)");
                    table.CheckConstraint("ck_email_verification_send_budgets_used", "used > 0");
                    table.CheckConstraint("ck_email_verification_send_budgets_window_start", "isfinite(window_start)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_verification_send_budgets");
        }
    }
}
