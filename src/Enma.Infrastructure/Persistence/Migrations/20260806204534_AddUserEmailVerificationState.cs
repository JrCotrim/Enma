using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserEmailVerificationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_email_verified_at",
                table: "users",
                sql: "email_verified_at IS NULL OR email_verified_at >= created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_email_verified_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "users");
        }
    }
}
