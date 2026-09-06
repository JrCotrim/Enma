using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancePaymentPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_payment_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    installment_count = table.Column<int>(type: "integer", nullable: false),
                    first_due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_payment_plans", x => x.id);
                    table.UniqueConstraint("ak_client_payment_plans_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_client_payment_plans_installment_count", "installment_count BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_client_payment_plans_installment_count_amount", "installment_count <= total_amount * 100");
                    table.CheckConstraint("ck_client_payment_plans_total_amount", "total_amount > 0");
                    table.ForeignKey(
                        name: "fk_client_payment_plans_clients_organization_id_client_id",
                        columns: x => new { x.organization_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_client_payment_plans_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_installments", x => x.id);
                    table.UniqueConstraint("ak_payment_installments_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_payment_installments_amount", "amount > 0");
                    table.CheckConstraint("ck_payment_installments_payment", "paid_at IS NULL OR paid_at >= created_at");
                    table.CheckConstraint("ck_payment_installments_sequence_number", "sequence_number > 0");
                    table.ForeignKey(
                        name: "fk_payment_installments_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_installments_payment_plans_tenant",
                        columns: x => new { x.organization_id, x.payment_plan_id },
                        principalTable: "client_payment_plans",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_payment_plans_organization_id_client_id_created_at_id",
                table: "client_payment_plans",
                columns: new[] { "organization_id", "client_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_installments_unpaid_organization_id_due_date_id",
                table: "payment_installments",
                columns: new[] { "organization_id", "due_date", "id" },
                filter: "paid_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payment_installments_plan_sequence",
                table: "payment_installments",
                columns: new[] { "organization_id", "payment_plan_id", "sequence_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_installments");

            migrationBuilder.DropTable(
                name: "client_payment_plans");
        }
    }
}
