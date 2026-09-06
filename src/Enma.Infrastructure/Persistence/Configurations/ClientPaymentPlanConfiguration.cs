using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class ClientPaymentPlanConfiguration
    : IEntityTypeConfiguration<ClientPaymentPlan>
{
    public void Configure(EntityTypeBuilder<ClientPaymentPlan> builder)
    {
        builder.ToTable(
            "client_payment_plans",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_client_payment_plans_total_amount",
                    "total_amount > 0");

                tableBuilder.HasCheckConstraint(
                    "ck_client_payment_plans_installment_count",
                    "installment_count BETWEEN 1 AND 120");

                tableBuilder.HasCheckConstraint(
                    "ck_client_payment_plans_installment_count_amount",
                    "installment_count <= total_amount * 100");
            });

        builder.HasKey(paymentPlan => paymentPlan.Id)
            .HasName("pk_client_payment_plans");

        builder.Property(paymentPlan => paymentPlan.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(paymentPlan => paymentPlan.OrganizationId)
            .HasColumnName("organization_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(paymentPlan => paymentPlan.ClientId)
            .HasColumnName("client_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(paymentPlan => paymentPlan.TotalAmount)
            .HasColumnName("total_amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(paymentPlan => paymentPlan.InstallmentCount)
            .HasColumnName("installment_count")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(paymentPlan => paymentPlan.FirstDueDate)
            .HasColumnName("first_due_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(paymentPlan => paymentPlan.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasAlternateKey(paymentPlan => new
            {
                paymentPlan.OrganizationId,
                paymentPlan.Id
            })
            .HasName(
                "ak_client_payment_plans_organization_id_id");

        builder.HasIndex(paymentPlan => new
            {
                paymentPlan.OrganizationId,
                paymentPlan.ClientId,
                paymentPlan.CreatedAt,
                paymentPlan.Id
            })
            .HasDatabaseName(
                "ix_client_payment_plans_organization_id_client_id_created_at_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(paymentPlan => paymentPlan.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_client_payment_plans_organizations_organization_id");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(paymentPlan => new
            {
                paymentPlan.OrganizationId,
                paymentPlan.ClientId
            })
            .HasPrincipalKey(client => new
            {
                client.OrganizationId,
                client.Id
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_client_payment_plans_clients_organization_id_client_id");

        builder.HasMany(paymentPlan => paymentPlan.Installments)
            .WithOne()
            .HasForeignKey(installment => new
            {
                installment.OrganizationId,
                installment.PaymentPlanId
            })
            .HasPrincipalKey(paymentPlan => new
            {
                paymentPlan.OrganizationId,
                paymentPlan.Id
            })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(
                "fk_payment_installments_payment_plans_tenant");

        builder.Navigation(paymentPlan => paymentPlan.Installments)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}