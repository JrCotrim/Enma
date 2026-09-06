using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class PaymentInstallmentConfiguration
    : IEntityTypeConfiguration<PaymentInstallment>
{
    public void Configure(EntityTypeBuilder<PaymentInstallment> builder)
    {
        builder.ToTable(
            "payment_installments",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_payment_installments_sequence_number",
                    "sequence_number > 0");

                tableBuilder.HasCheckConstraint(
                    "ck_payment_installments_amount",
                    "amount > 0");

                tableBuilder.HasCheckConstraint(
                    "ck_payment_installments_payment",
                    "paid_at IS NULL OR paid_at >= created_at");
            });

        builder.HasKey(installment => installment.Id)
            .HasName("pk_payment_installments");

        builder.Property(installment => installment.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(installment => installment.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(installment => installment.PaymentPlanId)
            .HasColumnName("payment_plan_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(installment => installment.SequenceNumber)
            .HasColumnName("sequence_number")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(installment => installment.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(installment => installment.DueDate)
            .HasColumnName("due_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(installment => installment.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(installment => installment.PaidAt)
            .HasColumnName("paid_at")
            .HasColumnType("timestamp with time zone");

        builder.HasAlternateKey(installment => new
            {
                installment.OrganizationId,
                installment.Id
            })
            .HasName(
                "ak_payment_installments_organization_id_id");

        builder.HasIndex(installment => new
            {
                installment.OrganizationId,
                installment.PaymentPlanId,
                installment.SequenceNumber
            })
            .IsUnique()
            .HasDatabaseName(
                "ux_payment_installments_plan_sequence");

        builder.HasIndex(installment => new
            {
                installment.OrganizationId,
                installment.DueDate,
                installment.Id
            })
            .HasDatabaseName(
                "ix_payment_installments_unpaid_organization_id_due_date_id")
            .HasFilter("paid_at IS NULL");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(installment => installment.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_payment_installments_organizations_organization_id");
    }
}