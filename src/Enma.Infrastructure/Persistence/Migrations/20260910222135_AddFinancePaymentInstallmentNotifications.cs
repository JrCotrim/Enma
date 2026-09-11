using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enma.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancePaymentInstallmentNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_exactly_one_source",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_kind",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_kind_source",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_occurrence",
                table: "notifications");

            migrationBuilder.AddColumn<Guid>(
                name: "payment_installment_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_installments_unpaid_due_date_organization_id_id",
                table: "payment_installments",
                columns: new[] { "due_date", "organization_id", "id" },
                filter: "paid_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_payment_installment_dedupe",
                table: "notifications",
                columns: new[] { "organization_id", "payment_installment_id", "recipient_user_id", "kind", "occurrence_date" },
                unique: true,
                filter: "payment_installment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_exactly_one_source",
                table: "notifications",
                sql: "num_nonnulls(legal_deadline_id, legal_task_id, calendar_event_id, payment_installment_id) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_kind",
                table: "notifications",
                sql: "kind IN (1, 2, 3, 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_kind_source",
                table: "notifications",
                sql: "(kind = 1 AND legal_deadline_id IS NOT NULL) OR (kind = 2 AND legal_task_id IS NOT NULL) OR (kind = 3 AND calendar_event_id IS NOT NULL) OR (kind = 4 AND payment_installment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_occurrence",
                table: "notifications",
                sql: "(kind IN (1, 2, 4) AND occurrence_date IS NOT NULL AND occurrence_at IS NULL) OR (kind = 3 AND occurrence_date IS NULL AND occurrence_at IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_installments_org_payment_installment_id",
                table: "notifications",
                columns: new[] { "organization_id", "payment_installment_id" },
                principalTable: "payment_installments",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notifications_installments_org_payment_installment_id",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_payment_installments_unpaid_due_date_organization_id_id",
                table: "payment_installments");

            migrationBuilder.DropIndex(
                name: "ux_notifications_payment_installment_dedupe",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_exactly_one_source",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_kind",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_kind_source",
                table: "notifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_occurrence",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "payment_installment_id",
                table: "notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_exactly_one_source",
                table: "notifications",
                sql: "num_nonnulls(legal_deadline_id, legal_task_id, calendar_event_id) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_kind",
                table: "notifications",
                sql: "kind IN (1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_kind_source",
                table: "notifications",
                sql: "(kind = 1 AND legal_deadline_id IS NOT NULL) OR (kind = 2 AND legal_task_id IS NOT NULL) OR (kind = 3 AND calendar_event_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_occurrence",
                table: "notifications",
                sql: "(kind IN (1, 2) AND occurrence_date IS NOT NULL AND occurrence_at IS NULL) OR (kind = 3 AND occurrence_date IS NULL AND occurrence_at IS NOT NULL)");
        }
    }
}
